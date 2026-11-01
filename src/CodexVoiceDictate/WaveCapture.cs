using System.Buffers;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace CodexVoiceDictate;

/// <summary>Encodes microphone PCM into Ogg/Opus as it arrives; only compressed audio stays in memory.</summary>
internal sealed class WaveCapture : IDisposable
{
    // The transcription endpoint accepts uploads up to 25 MB. Leave room for Ogg
    // page buffering, the final page, and multipart form metadata.
    public const int MaxEncodedBytes = 24_000_000;
    private const int FinalizationReserveBytes = 128 * 1024;

    private MemoryStream _audio = null!;
    private WaveInEvent _input = null!;
    private OpusOggWriteStream _writer = null!;
    private readonly TaskCompletionSource<byte[]> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Diagnostics.Stopwatch _duration = new();
    private volatile bool _cancelled;
    private volatile bool _limitReached;
    private bool _stopped;
    private Exception? _encodingError;

    public TimeSpan Duration => _duration.Elapsed;
    public bool LimitReached => _limitReached;

    public WaveCapture()
    {
        Exception? lastError = null;
        foreach (int sampleRate in new[] { 48000, 16000, 44100 })
        {
            var audio = new MemoryStream();
            WaveInEvent? input = null;
            try
            {
                input = new WaveInEvent
                {
                    DeviceNumber = -1,
                    WaveFormat = new WaveFormat(sampleRate, 16, 1),
                    BufferMilliseconds = 100
                };
                var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
                encoder.Bitrate = 24000;
                var writer = new OpusOggWriteStream(encoder, audio, inputSampleRate: sampleRate, leaveOpen: true);

                _audio = audio;
                _input = input;
                _writer = writer;
                input.DataAvailable += OnDataAvailable;
                input.RecordingStopped += OnRecordingStopped;
                input.StartRecording();
                _duration.Start();
                return;
            }
            catch (Exception error)
            {
                lastError = error;
                if (input != null)
                {
                    input.DataAvailable -= OnDataAvailable;
                    input.RecordingStopped -= OnRecordingStopped;
                    input.Dispose();
                }
                audio.Dispose();
            }
        }
        throw new InvalidOperationException("No compatible microphone capture format is available.", lastError);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_cancelled || _limitReached) return;
        int sampleCount = args.BytesRecorded / sizeof(short);
        if (sampleCount == 0) return;

        short[] samples = ArrayPool<short>.Shared.Rent(sampleCount);
        try
        {
            Buffer.BlockCopy(args.Buffer, 0, samples, 0, sampleCount * sizeof(short));
            lock (_audio)
            {
                if (_cancelled || _limitReached) return;
                _writer.WriteSamples(samples, 0, sampleCount);
                // Stop accepting PCM now; the UI timer ends the capture on its
                // next tick, without stopping WaveInEvent on its callback thread.
                if (_audio.Length >= MaxEncodedBytes - FinalizationReserveBytes)
                    _limitReached = true;
            }
        }
        catch (Exception error)
        {
            lock (_audio) { _encodingError = error; _limitReached = true; }
        }
        finally { ArrayPool<short>.Shared.Return(samples); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _duration.Stop();
        try
        {
            lock (_audio)
            {
                if (_cancelled) { _finished.TrySetResult([]); return; }
                if (_encodingError is { } error) throw new IOException("Audio encoding failed.", error);
                if (args.Exception is { } captureError) throw new IOException("Microphone recording failed.", captureError);

                _writer.Finish(); // Flushes the final Ogg/Opus packet and end-of-stream marker.
                if (_audio.Length > MaxEncodedBytes)
                    throw new IOException("Compressed recording exceeded the OpenAI upload limit.");
                _finished.TrySetResult(_audio.ToArray());
            }
        }
        catch (Exception error) { _finished.TrySetException(error); }
    }

    public async Task<byte[]> StopAsync()
    {
        bool shouldStop;
        lock (_audio) { shouldStop = !_stopped; _stopped = true; }
        if (shouldStop) _input.StopRecording();
        return await _finished.Task.ConfigureAwait(false);
    }

    public void Cancel()
    {
        _cancelled = true;
        bool shouldStop;
        lock (_audio) { shouldStop = !_stopped; _stopped = true; }
        if (shouldStop) _input.StopRecording();
    }

    public void Dispose()
    {
        Cancel();
        _input.DataAvailable -= OnDataAvailable;
        _input.RecordingStopped -= OnRecordingStopped;
        _input.Dispose();
        _audio.Dispose();
    }
}
