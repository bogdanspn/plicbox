using System.Windows.Markup;
using Plicbox.Core;

namespace Plicbox.App;

/// <summary>
/// Text in the app's language, written in XAML as its English: <c>Header="{local:T 'Mark as Read'}"</c>.
/// Also for format strings (<c>HeaderStringFormat="{local:T 'Block {0}…'}"</c>). See <see cref="L"/>.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
