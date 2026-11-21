using System;
using System.IO;
using System.Text;

namespace Service
{
    // The isolated helper and service exchange bounded, in-memory frames over loopback only.
    static class ProbeWire
    {
        public const byte Stop = 0;
        public const byte Capture = 1;
        private const byte Frame = 0;
        private const byte CaptureError = 1;
        private const byte Unavailable = 2;
        private const int MaximumErrorBytes = 512;

        public static void WriteFrame(Stream stream, PixelFrame frame)
        {
            if (frame.Width > Nv12FrameConverter.MaxOutputWidth || frame.Height > Nv12FrameConverter.MaxOutputHeight)
                throw new InvalidDataException("probe frame exceeds the output limit");
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Frame);
            writer.Write(frame.Width);
            writer.Write(frame.Height);
            writer.Write(frame.Rgb.Length);
            writer.Write(frame.Rgb);
            writer.Flush();
        }

        public static void WriteError(Stream stream, string error, bool unavailable)
        {
            byte[] text = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(error) ? "video capture unavailable" : error);
            if (text.Length > MaximumErrorBytes) Array.Resize(ref text, MaximumErrorBytes);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(unavailable ? Unavailable : CaptureError);
            writer.Write(text.Length);
            writer.Write(text);
            writer.Flush();
        }

        public static bool ReadReply(Stream stream, out PixelFrame frame, out string error, out bool unavailable)
        {
            frame = null;
            error = null;
            unavailable = false;
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            byte kind = reader.ReadByte();
            if (kind == CaptureError || kind == Unavailable)
            {
                int size = reader.ReadInt32();
                if (size < 1 || size > MaximumErrorBytes) throw new InvalidDataException("invalid probe error length");
                error = Encoding.UTF8.GetString(ReadExactly(reader, size));
                unavailable = kind == Unavailable;
                return false;
            }
            if (kind != Frame) throw new InvalidDataException("invalid probe reply");
            int width = reader.ReadInt32(), height = reader.ReadInt32(), length = reader.ReadInt32();
            if (width < 1 || width > Nv12FrameConverter.MaxOutputWidth ||
                height < 1 || height > Nv12FrameConverter.MaxOutputHeight || length != width * height * 3)
                throw new InvalidDataException("invalid probe frame dimensions");
            frame = new PixelFrame(width, height, ReadExactly(reader, length));
            return true;
        }

        private static byte[] ReadExactly(BinaryReader reader, int length)
        {
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException("probe disconnected during a reply");
            return bytes;
        }
    }
}
