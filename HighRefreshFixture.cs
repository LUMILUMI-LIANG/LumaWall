using System.Text;

namespace LumaWall;

// An indexed, uncompressed AVI contains distinct real frames at 240fps without
// depending on the operating system encoder's maximum accepted H.264 rate.
static class HighRefreshFixture
{
    public static void CreateAvi(string path, int width, int height, int fps, int seconds)
    {
        int bytes = checked(width * height * 4), frames = checked(fps * seconds);
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        void Four(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
        long Start(string id) { Four(id); long size = stream.Position; writer.Write(0); return size; }
        void Finish(long position) { long end = stream.Position; stream.Position = position; writer.Write(checked((int)(end - position - 4))); stream.Position = end; }
        long riff = Start("RIFF"); Four("AVI ");
        long header = Start("LIST"); Four("hdrl");
        Four("avih"); writer.Write(56); writer.Write(1000000 / fps); writer.Write(checked(bytes * fps)); writer.Write(0); writer.Write(0x10);
        writer.Write(frames); writer.Write(0); writer.Write(1); writer.Write(bytes); writer.Write(width); writer.Write(height);
        for (int i = 0; i < 4; i++) writer.Write(0);
        long video = Start("LIST"); Four("strl");
        Four("strh"); writer.Write(56); Four("vids"); Four("DIB "); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(1); writer.Write(fps); writer.Write(0); writer.Write(frames); writer.Write(bytes); writer.Write(-1); writer.Write(0);
        writer.Write((short)0); writer.Write((short)0); writer.Write((short)width); writer.Write((short)height);
        Four("strf"); writer.Write(40); writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((short)1); writer.Write((short)32);
        writer.Write(0); writer.Write(bytes); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        Finish(video); Finish(header);
        long movie = Start("LIST"); long origin = stream.Position; Four("movi");
        var offsets = new int[frames]; byte[] pixels = new byte[bytes];
        for (int frame = 0; frame < frames; frame++)
        {
            int stripe = frame * 3 % (width - 24);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int index = ((height - 1 - y) * width + x) * 4;
                bool moving = x >= stripe && x < stripe + 24;
                pixels[index] = moving ? (byte)32 : (byte)48; pixels[index + 1] = moving ? (byte)210 : (byte)68;
                pixels[index + 2] = moving ? (byte)245 : (byte)96; pixels[index + 3] = 255;
            }
            offsets[frame] = checked((int)(stream.Position - origin)); Four("00db"); writer.Write(bytes); writer.Write(pixels);
        }
        Finish(movie);
        Four("idx1"); writer.Write(checked(frames * 16));
        foreach (int offset in offsets) { Four("00db"); writer.Write(0x10); writer.Write(offset); writer.Write(bytes); }
        Finish(riff);
    }
}
