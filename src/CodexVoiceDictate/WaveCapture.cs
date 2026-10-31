using NAudio.Wave;

namespace CodexVoiceDictate;

/// <summary>Microphone audio remains in memory. No temporary recordings are written to disk.</summary>
internal sealed class WaveCapture : IDisposable
{
    private readonly MemoryStream _audio = new();
    private readonly WaveInEvent _input;
    private readonly WaveFileWriter _writer;
    private readonly TaskCompletionSource<byte[]> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Diagnostics.Stopwatch _duration = new();
    private bool _cancelled;
    private bool _stopped;

    public TimeSpan Duration => _duration.Elapsed;

    public WaveCapture()
    {
        Exception? lastError = null;
        foreach (int sampleRate in new[] { 16000, 48000, 44100 })
        {
            WaveInEvent? input = null;
            try
            {
                input = new WaveInEvent { DeviceNumber = -1, WaveFormat = new WaveFormat(sampleRate, 16, 1), BufferMilliseconds = 100 };
                // The driver is opened here to allow a fallback to another supported sample rate.
                input.StartRecording();
                _input = input;
                _writer = new WaveFileWriter(_audio, input.WaveFormat);
                _input.DataAvailable += OnDataAvailable;
                _input.RecordingStopped += OnRecordingStopped;
                _duration.Start();
                return;
            }
            catch (Exception error) when (error is MmException or InvalidOperationException)
            {
                lastError = error;
                input?.Dispose();
            }
        }
        throw new InvalidOperationException("No compatible microphone capture format is available.", lastError);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_cancelled) return;
        lock (_audio) _writer.Write(args.Buffer, 0, args.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _duration.Stop();
        try
        {
            lock (_audio)
            {
                // Closing WaveFileWriter finalizes RIFF chunk lengths in the WAV header.
                _writer.Dispose();
                _finished.TrySetResult(_cancelled ? [] : _audio.ToArray());
            }
        }
        catch (Exception error) { _finished.TrySetException(error); }
    }

    public async Task<byte[]> StopAsync()
    {
        if (!_stopped) { _stopped = true; _input.StopRecording(); }
        return await _finished.Task.ConfigureAwait(false);
    }

    public void Cancel()
    {
        _cancelled = true;
        if (!_stopped) { _stopped = true; _input.StopRecording(); }
    }

    public void Dispose()
    {
        Cancel();
        _input.DataAvailable -= OnDataAvailable;
        _input.RecordingStopped -= OnRecordingStopped;
        _input.Dispose();
        _writer.Dispose();
        _audio.Dispose();
    }
}
