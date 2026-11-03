using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceNoteTranscriber;

/// <summary>Turns any voice note into what Whisper wants: 16 kHz mono float samples.</summary>
public static partial class AudioDecoder
{
    public const int SampleRate = 16000;

    public static float[] Decode(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        ISampleProvider source;
        IDisposable? owner = null;
        try
        {
            if (ext is ".opus" or ".ogg" or ".oga")
            {
                source = new ArraySampleProvider(DecodeOpus(path), 48000);
            }
            else
            {
                var reader = new MediaFoundationReader(path);
                owner = reader;
                source = reader.ToSampleProvider();
                if (source.WaveFormat.Channels == 2)
                    source = source.ToMono();
                else if (source.WaveFormat.Channels > 2)
                    source = new FirstChannel(source);
            }
            if (source.WaveFormat.SampleRate != SampleRate)
                source = new WdlResamplingSampleProvider(source, SampleRate);
            return ReadAll(source);
        }
        finally
        {
            owner?.Dispose();
        }
    }

    /// <summary>Ogg Opus at 48 kHz, mixed down to mono by libopus.</summary>
    private static float[] DecodeOpus(string path)
    {
        var packets = OggPackets(File.ReadAllBytes(path));
        if (packets.Count < 2 || !packets[0].AsSpan().StartsWith("OpusHead"u8))
            throw new InvalidDataException("This isn't an Opus voice note.");
        // Encoder warm-up samples at the start, to be dropped.
        int preSkip = packets[0].Length >= 12 ? BitConverter.ToUInt16(packets[0], 10) : 0;

        IntPtr decoder = Opus.opus_decoder_create(48000, 1, out int error);
        if (error != 0 || decoder == IntPtr.Zero)
            throw new InvalidOperationException($"Couldn't start the Opus decoder (error {error}).");
        try
        {
            var samples = new List<float>();
            var frame = new float[MaxFrame];
            // Packets 0 and 1 are the OpusHead and OpusTags headers.
            for (int i = 2; i < packets.Count; i++)
            {
                int n = Opus.opus_decode_float(decoder, packets[i], packets[i].Length, frame, MaxFrame, 0);
                if (n > 0) // a damaged packet is skipped; one is 120 ms at most
                    samples.AddRange(new ArraySegment<float>(frame, 0, n));
            }
            return samples.Count > preSkip ? samples.GetRange(preSkip, samples.Count - preSkip).ToArray() : [];
        }
        finally
        {
            Opus.opus_decoder_destroy(decoder);
        }
    }

    // 120 ms at 48 kHz, the longest an Opus packet can be.
    private const int MaxFrame = 5760;

    /// <summary>
    /// Splits an Ogg file into its packets: each page has a table of segment
    /// lengths, and a segment shorter than 255 bytes ends a packet (which may
    /// have started on an earlier page).
    /// </summary>
    private static List<byte[]> OggPackets(byte[] data)
    {
        var packets = new List<byte[]>();
        var current = new List<byte>();
        int pos = 0;
        while (pos + 27 <= data.Length && data.AsSpan(pos, 4).SequenceEqual("OggS"u8))
        {
            int segments = data[pos + 26];
            int body = pos + 27 + segments;
            for (int i = 0; i < segments && body <= data.Length; i++)
            {
                int length = Math.Min(data[pos + 27 + i], data.Length - body); // a cut-off file ends early
                current.AddRange(new ArraySegment<byte>(data, body, length));
                body += length;
                if (data[pos + 27 + i] < 255)
                {
                    packets.Add(current.ToArray());
                    current.Clear();
                }
            }
            pos = body;
        }
        return packets;
    }

    /// <summary>libopus, from the Concentus.Native.NetCore package.</summary>
    private static partial class Opus
    {
        [LibraryImport("opus")]
        public static partial IntPtr opus_decoder_create(int sampleRate, int channels, out int error);

        [LibraryImport("opus")]
        public static partial int opus_decode_float(IntPtr decoder, byte[] data, int length, float[] pcm,
                                                    int frameSize, int decodeFec);

        [LibraryImport("opus")]
        public static partial void opus_decoder_destroy(IntPtr decoder);
    }

    private static float[] ReadAll(ISampleProvider source)
    {
        var all = new List<float>();
        var buffer = new float[SampleRate];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            all.AddRange(new ArraySegment<float>(buffer, 0, read));
        return all.ToArray();
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    /// <summary>Surround audio (rare in a voice note): keep the first channel.</summary>
    private sealed class FirstChannel(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            if (_buffer.Length < count * _channels)
                _buffer = new float[count * _channels];
            int frames = source.Read(_buffer, 0, count * _channels) / _channels;
            for (int i = 0; i < frames; i++)
                buffer[offset + i] = _buffer[i * _channels];
            return frames;
        }
    }
}
