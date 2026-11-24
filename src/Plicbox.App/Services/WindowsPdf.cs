using System.Runtime.InteropServices;

namespace Plicbox.App.Services;

/// <summary>
/// A PDF opened with Windows' own renderer (Windows.Data.Pdf), for the print preview's pages. Called through COM
/// directly: the .NET binding for Windows' APIs (Microsoft.Windows.SDK.NET.dll) is 53 MB, and this is the only
/// Windows API Plicbox uses. Slots and IIDs are from the Windows SDK's metadata (Windows.winmd); every call is made
/// on the thread that opened the document (the print window's).
/// </summary>
public sealed class WindowsPdf : IDisposable
{
    private static readonly Guid IPdfDocumentStatics = new("433a0b5f-c007-4788-90f2-08143d922599");
    private static readonly Guid IPdfPageRenderOptions = new("3c98056f-b7cf-4c29-9a04-52d90267f425");
    private static readonly Guid IRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IStream = new("0000000c-0000-0000-c000-000000000046");
    private static readonly Guid IAsyncInfo = new("00000036-0000-0000-c000-000000000046");
    private static readonly Guid IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    private static readonly Guid BmpEncoder = new("69be8bb4-d66d-47c8-865a-ed1589433782"); // BitmapEncoder.BmpEncoderId (WIC's BMP encoder)

    private nint _document; // IPdfDocument

    public uint PageCount { get; }

    private WindowsPdf(nint document)
    {
        _document = document;
        PageCount = Com.GetUInt(document, 7); // get_PageCount
    }

    public static async Task<WindowsPdf> LoadAsync(byte[] pdf)
    {
        var stream = StreamOver(pdf);
        var statics = Factory("Windows.Data.Pdf.PdfDocument", IPdfDocumentStatics);
        nint operation;
        try { operation = Com.CallOut(statics, 8, stream); } // LoadFromStreamAsync(IRandomAccessStream) -> IAsyncOperation<PdfDocument>
        finally
        {
            Marshal.Release(statics);
            Marshal.Release(stream); // the document holds its own reference
        }
        try
        {
            await WaitAsync(operation);
            return new WindowsPdf(Com.GetPointer(operation, 8)); // GetResults
        }
        finally { Finish(operation); }
    }

    /// <summary>The page's size, in 1/96 inch.</summary>
    public (double Width, double Height) PageSize(uint index)
    {
        var page = Page(index);
        try { return Com.GetSize(page, 10); } // get_Size
        finally { ClosePage(page); }
    }

    /// <summary>The page as a BMP picture this many pixels wide; null when the document was closed meanwhile.</summary>
    public async Task<byte[]?> RenderAsync(uint index, uint pixelWidth)
    {
        if (_document == 0) return null;
        var page = Page(index);
        nint output = 0, options = 0, action = 0, reader = 0;
        try
        {
            output = Instance("Windows.Storage.Streams.InMemoryRandomAccessStream", IRandomAccessStream);
            options = Instance("Windows.Data.Pdf.PdfPageRenderOptions", IPdfPageRenderOptions);
            Com.SetUInt(options, 9, pixelWidth); // put_DestinationWidth
            Com.SetGuid(options, 17, BmpEncoder); // put_BitmapEncoderId (PNG otherwise: slower to make and to read)
            action = Com.CallOut(page, 7, output, options); // RenderToStreamAsync(IRandomAccessStream, PdfPageRenderOptions) -> IAsyncAction
            await WaitAsync(action);
            Com.Call(action, 8); // GetResults
            Finish(action);
            action = 0;
            reader = Com.StreamOverRandomAccessStream(output, IStream);
            return Com.ReadAll(reader);
        }
        finally
        {
            if (reader != 0) Marshal.Release(reader);
            if (action != 0) Finish(action);
            if (options != 0) Marshal.Release(options);
            if (output != 0) Marshal.Release(output);
            ClosePage(page);
        }
    }

    public void Dispose()
    {
        if (_document == 0) return;
        Marshal.Release(_document);
        _document = 0;
    }

    private nint Page(uint index)
    {
        ObjectDisposedException.ThrowIf(_document == 0, this);
        return Com.GetPointer(_document, 6, index); // GetPage
    }

    private static void ClosePage(nint page)
    {
        if (Marshal.QueryInterface(page, in IClosable, out var closable) == 0)
        {
            Com.Call(closable, 6); // Close
            Marshal.Release(closable);
        }
        Marshal.Release(page);
    }

    /// <summary>Waits for a Windows async operation or action (polled on this thread), and throws its error, if any.</summary>
    private static async Task WaitAsync(nint async)
    {
        Com.Check(Marshal.QueryInterface(async, in IAsyncInfo, out var info));
        try
        {
            while (true)
            {
                switch (Com.GetInt(info, 7)) // get_Status
                {
                    case 1: return; // Completed
                    case 2: throw new OperationCanceledException();
                    case 3: Com.Check(Com.GetInt(info, 8)); return; // Error: get_ErrorCode
                }
                await Task.Delay(4);
            }
        }
        finally { Marshal.Release(info); }
    }

    /// <summary>Closes an async operation or action (after its results were read: closing first makes them unreadable) and lets go of it.</summary>
    private static void Finish(nint async)
    {
        if (Marshal.QueryInterface(async, in IAsyncInfo, out var info) == 0)
        {
            try { Com.Call(info, 10); } // Close
            catch (COMException) { } // still running (after an error elsewhere): letting go is enough
            Marshal.Release(info);
        }
        Marshal.Release(async);
    }

/// <summary>Windows' own in-memory stream holding these bytes, at its start (pages are drawn from clones of it).</summary>
    private static nint StreamOver(byte[] bytes)
    {
        var stream = Instance("Windows.Storage.Streams.InMemoryRandomAccessStream", IRandomAccessStream);
        try
        {
            var writer = Com.StreamOverRandomAccessStream(stream, IStream);
            try { Com.WriteAll(writer, bytes); }
            finally { Marshal.Release(writer); }
            Com.Seek(stream, 11, 0); // IRandomAccessStream.Seek(0)
            return stream;
        }
        catch
        {
            Marshal.Release(stream);
            throw;
        }
    }

    private static nint Factory(string runtimeClass, Guid iid)
    {
        var name = Com.HString(runtimeClass);
        try
        {
            Com.Check(RoGetActivationFactory(name, iid, out var factory));
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    private static nint Instance(string runtimeClass, Guid iid)
    {
        var name = Com.HString(runtimeClass);
        try
        {
            Com.Check(RoActivateInstance(name, out var inspectable));
            try
            {
                Com.Check(Marshal.QueryInterface(inspectable, in iid, out var result));
                return result;
            }
            finally { Marshal.Release(inspectable); }
        }
        finally { WindowsDeleteString(name); }
    }

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [DllImport("combase.dll")]
    private static extern int RoActivateInstance(nint activatableClassId, out nint instance);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    /// <summary>Calls by vtable slot; each returns an HRESULT, checked here.</summary>
    private static unsafe class Com
    {
        private static void* Slot(nint obj, int slot) => (*(void***)obj)[slot];

        public static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        public static void Call(nint obj, int slot) =>
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, slot))(obj));

        public static void SetUInt(nint obj, int slot, uint value) =>
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(obj, slot))(obj, value));

        public static void SetGuid(nint obj, int slot, Guid value) =>
            Check(((delegate* unmanaged[Stdcall]<nint, Guid, int>)Slot(obj, slot))(obj, value));

        public static int GetInt(nint obj, int slot)
        {
            int value;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(obj, slot))(obj, &value));
            return value;
        }

        public static uint GetUInt(nint obj, int slot)
        {
            uint value;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(obj, slot))(obj, &value));
            return value;
        }

        public static (double, double) GetSize(nint obj, int slot)
        {
            float* size = stackalloc float[2];
            Check(((delegate* unmanaged[Stdcall]<nint, float*, int>)Slot(obj, slot))(obj, size));
            return (size[0], size[1]);
        }

        public static nint GetPointer(nint obj, int slot)
        {
            nint value;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, slot))(obj, &value));
            return value;
        }

        public static nint GetPointer(nint obj, int slot, uint argument)
        {
            nint value;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(obj, slot))(obj, argument, &value));
            return value;
        }

        public static nint CallOut(nint obj, int slot, nint argument)
        {
            nint value;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(obj, slot))(obj, argument, &value));
            return value;
        }

        public static nint CallOut(nint obj, int slot, nint first, nint second)
        {
            nint value;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(obj, slot))(obj, first, second, &value));
            return value;
        }

        public static void Seek(nint obj, int slot, ulong position) =>
            Check(((delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(obj, slot))(obj, position));

        /// <summary>Writes all of these bytes to an IStream.</summary>
        public static void WriteAll(nint stream, byte[] bytes)
        {
            var write = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)Slot(stream, 4);
            var done = 0;
            fixed (byte* start = bytes)
                while (done < bytes.Length)
                {
                    uint count;
                    Check(write(stream, start + done, (uint)(bytes.Length - done), &count));
                    if (count == 0) throw new System.IO.IOException("Writing the PDF to memory stopped.");
                    done += (int)count;
                }
        }

        public static nint StreamOverRandomAccessStream(nint stream, Guid iid)
        {
            Check(CreateStreamOverRandomAccessStream(stream, iid, out var result));
            return result;
        }

        /// <summary>Everything in an IStream, from the start.</summary>
        public static byte[] ReadAll(nint stream)
        {
            var seek = (delegate* unmanaged[Stdcall]<nint, long, int, long*, int>)Slot(stream, 5);
            long end;
            Check(seek(stream, 0, 2, &end)); // STREAM_SEEK_END
            Check(seek(stream, 0, 0, null)); // STREAM_SEEK_SET
            var bytes = new byte[end];
            var done = 0;
            var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)Slot(stream, 3);
            fixed (byte* start = bytes)
                while (done < bytes.Length)
                {
                    uint count;
                    Check(read(stream, start + done, (uint)(bytes.Length - done), &count));
                    if (count == 0) break;
                    done += (int)count;
                }
            return done == bytes.Length ? bytes : bytes[..done];
        }

        public static nint HString(string text)
        {
            Check(WindowsCreateString(text, (uint)text.Length, out var hstring));
            return hstring;
        }

        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, uint length, out nint hstring);

        [DllImport("shcore.dll")]
        private static extern int CreateStreamOverRandomAccessStream(nint randomAccessStream, in Guid iid, out nint result);
    }
}
