using System;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Bmp;

class Program {
    static void Main() {
        Console.WriteLine("JPEG: " + string.Join(",", JpegFormat.Instance.FileExtensions));
        Console.WriteLine("PNG: " + string.Join(",", PngFormat.Instance.FileExtensions));
        Console.WriteLine("BMP: " + string.Join(",", BmpFormat.Instance.FileExtensions));
    }
}
