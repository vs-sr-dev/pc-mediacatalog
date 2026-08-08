using System.Globalization;
using System.Windows.Data;
using MediaCatalog.App.ViewModels;

namespace MediaCatalog.App.Infrastructure;

/// <summary>
/// Reads one plugin field out of a row, for the grid columns plugins bring with them.
///
/// A converter rather than a binding path because the fields are not properties: which ones
/// exist is decided by whatever DLLs are sitting in the plugins folder, and there is no way
/// to write a property for a column whose name nobody knew when the program was built. The
/// column carries its field's label as the converter parameter, which is the same name the
/// filter bar and the consolidation rules use for it.
/// </summary>
public sealed class PluginFieldColumn : IValueConverter
{
    public static readonly PluginFieldColumn Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FileRow row && parameter is string label ? row.PluginValue(label) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A plugin's fields are read from the file, not typed in.");
}
