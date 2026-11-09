namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileFormatValidationOptionsTests
{
    [TestMethod]
    public void Empty_Throws()
    {
        Should.Throw<ArgumentException>(() => new FileFormatValidationOptions());
    }

    [TestMethod]
    public void Null_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new FileFormatValidationOptions((IEnumerable<FileFormat>)null!));
    }

    [TestMethod]
    public void DuplicateExtensions_Throws()
    {
        var custom = FileFormat.AnyContent(".jpg");

        // Built-in JPEG also claims .jpg.
        var ex = Should.Throw<ArgumentException>(() => new FileFormatValidationOptions(FileFormat.Jpeg, custom));
        ex.Message.ShouldContain(".jpg");
    }

    [TestMethod]
    public void SeveralProblems_ListsEvery()
    {
        var ex = Should.Throw<ArgumentException>(() => new FileFormatValidationOptions(
            FileFormat.Jpeg, null!, FileFormat.AnyContent(".jpg"), FileFormat.AnyContent(".png"), FileFormat.Png));

        ex.ParamName.ShouldBe("allowedFormats");
        ex.Message.ShouldStartWith("The value contains null. The value contains the extension '.jpg' in more than one file format: ");
        ex.Message.ShouldContain("The value contains the extension '.png' in more than one file format: ");
    }

    [TestMethod]
    public void ContainsNull_Throws()
    {
        Should.Throw<ArgumentException>(() => new FileFormatValidationOptions(FileFormat.Jpeg, null!));
    }

    [TestMethod]
    public void Ctor_StoresAllowedFormats()
    {
        var options = new FileFormatValidationOptions(FileFormat.Jpeg, FileFormat.Png);
        options.AllowedFormats.ShouldBe([FileFormat.Jpeg, FileFormat.Png]);
    }
}
