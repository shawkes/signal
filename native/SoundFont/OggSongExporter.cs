using OggVorbisEncoder;
using Signal.Native.Engine;

namespace Signal.Native.SoundFont;

public static class OggSongExporter
{
    private const int BlockFrames = 1024;

    public static int NominalBitrate(int sampleRate, float quality) =>
        VorbisInfo.InitVariableBitRate(2, sampleRate, quality).BitRateNominal / 1000;

    public static void Export(string path, Song song, MeltySynth.SoundFont bank, CancellationToken cancellationToken,
        int sampleRate = 44_100, float quality = 0.5f)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".signal-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                Encode(output, song, bank, sampleRate, quality, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
                File.Delete(temporary);
        }
    }

    private static void Encode(Stream output, Song song, MeltySynth.SoundFont bank, int sampleRate, float quality,
        CancellationToken cancellationToken)
    {
        var renderer = new SongAudioRenderer(song, new SoundFontPiano(bank, sampleRate, generalMidi: true), sampleRate);
        var info = VorbisInfo.InitVariableBitRate(2, sampleRate, quality);
        var stream = new OggStream(Random.Shared.Next());
        stream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        stream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        stream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        WritePages(force: true);

        var encoder = ProcessingState.Create(info);
        var stereo = new float[2 * BlockFrames];
        float[][] channels = [new float[BlockFrames], new float[BlockFrames]];
        int frames;
        while ((frames = renderer.ReadFrames(stereo)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < frames; i++)
            {
                channels[0][i] = stereo[2 * i];
                channels[1][i] = stereo[2 * i + 1];
            }
            encoder.WriteData(channels, frames, 0);
            WritePackets();
        }
        // OggVorbisEncoder 1.2.2 undercounts the end granule by half its long window.
        int overlap = info.CodecSetup.BlockSizes[1] / 2;
        encoder.WriteData([new float[overlap], new float[overlap]], overlap, 0);
        encoder.WriteEndOfStream();
        WritePackets();
        WritePages(force: true);

        void WritePackets()
        {
            while (!stream.Finished && encoder.PacketOut(out var packet))
            {
                stream.PacketIn(packet);
                WritePages(force: false);
            }
        }

        void WritePages(bool force)
        {
            while (stream.PageOut(out var page, force))
            {
                output.Write(page.Header);
                output.Write(page.Body);
            }
        }
    }
}
