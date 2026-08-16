using PdfSharp.Fonts;

namespace Sidwell.Backend.Infrastructure.Implementations.Reports;

public sealed class LiberationFontResolver : IFontResolver
{
    public const string FamilyName = "Liberation Sans";

    private const string BasePath = "/usr/share/fonts/truetype/liberation/";

    private static readonly Dictionary<string, string> FileByFace = new()
    {
        ["regular"] = "LiberationSans-Regular.ttf",
        ["bold"] = "LiberationSans-Bold.ttf",
        ["italic"] = "LiberationSans-Italic.ttf",
        ["bolditalic"] = "LiberationSans-BoldItalic.ttf"
    };

    public byte[] GetFont(string faceName)
    {
        string fileName = FileByFace.TryGetValue(faceName, out string? f) ? f : FileByFace["regular"];
        string path = Path.Combine(BasePath, fileName);

        return File.Exists(path)
            ? File.ReadAllBytes(path)
            : throw new FileNotFoundException(
                $"Font file '{fileName}' not found at '{path}'. Ensure the 'fonts-liberation' package is installed in the runtime image.",
                path);
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        string face = (isBold, isItalic) switch
        {
            (true, true) => "bolditalic",
            (true, false) => "bold",
            (false, true) => "italic",
            _ => "regular"
        };

        return new FontResolverInfo(face);
    }
}
