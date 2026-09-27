using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GameZh.OcrBridge
{
    static class Program
    {
        static int Main(string[] args)
        {
            try {
                if (args.Length > 0 && args[0] == "--languages") {
                    var tags = new List<string>();
                    foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
                        tags.Add(language.LanguageTag);
                    Console.WriteLine(string.Join(",", tags.ToArray()));
                    return 0;
                }
                Console.InputEncoding = Encoding.UTF8;
                Console.OutputEncoding = Encoding.UTF8;
                string header;
                while ((header = Console.ReadLine()) != null) {
                    string encoded = Console.ReadLine();
                    if (encoded == null) break;
                    try {
                        string[] parts = header.Split('|');
                        if (parts.Length != 3) throw new Exception("请求格式错误");
                        int width = int.Parse(parts[0]);
                        int height = int.Parse(parts[1]);
                        if (width < 1 || height < 1 || width > 3000 || height > 3000)
                            throw new Exception("图片尺寸无效");
                        byte[] pixels = Convert.FromBase64String(encoded);
                        if (pixels.Length != (long)width * height * 4) throw new Exception("图片数据长度错误");
                        string text = Recognize(pixels, width, height, parts[2]);
                        Console.WriteLine("OK:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
                    } catch (Exception ex) {
                        Console.WriteLine("ERR:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ex.Message)));
                    }
                    Console.Out.Flush();
                }
                return 0;
            } catch (Exception ex) {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        static string Recognize(byte[] pixels, int width, int height, string tag)
        {
            OcrEngine engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
            if (engine == null) throw new Exception("缺少 " + tag + " Windows OCR 语言组件");
            IBuffer buffer;
            using (var writer = new DataWriter()) {
                writer.WriteBytes(pixels);
                buffer = writer.DetachBuffer();
            }
            using (var image = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore)) {
                var operation = engine.RecognizeAsync(image);
                while (operation.Status == Windows.Foundation.AsyncStatus.Started) Thread.Sleep(5);
                var result = operation.GetResults();
                var lines = new List<string>();
                foreach (var line in result.Lines) lines.Add(line.Text);
                return string.Join("\n", lines.ToArray());
            }
        }
    }
}
