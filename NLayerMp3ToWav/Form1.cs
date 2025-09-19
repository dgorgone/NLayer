using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using NLayer;

namespace NLayerMp3ToWav
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        // Main button handler for MP3 to WAV conversion
        private void buttonConvert_Click(object sender, EventArgs e)
        {
            try
            {
                var ofd = new OpenFileDialog
                {
                    Filter = "MP3 Files|*.mp3",
                    Title = "Select an MP3 file to decode"
                };
                if (ofd.ShowDialog() != DialogResult.OK)
                {
                    Debug.WriteLine("User cancelled file selection.");
                    return;
                }

                string mp3Path = ofd.FileName;
                string wavFileName = Path.GetFileNameWithoutExtension(mp3Path) + ".wav";
                string wavPath = Path.Combine(Path.GetTempPath(), wavFileName);

                Debug.WriteLine($"Input MP3: {mp3Path}");
                Debug.WriteLine($"Output WAV: {wavPath}");

                using (var mp3Stream = File.OpenRead(mp3Path))
                using (var mpegFile = new MpegFile(mp3Stream))
                {
                    // MPEG Layer check (Layer 3 is MP3)
                    int mpegLayer = GetMpegLayer(mp3Stream);
                    if (mpegLayer != 3)
                    {
                        MessageBox.Show($"Warning: This file is MPEG Layer {mpegLayer}. Only Layer 3 (MP3) is fully supported. Output may be corrupt.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Debug.WriteLine($"Warning: Detected MPEG Layer {mpegLayer}. Only Layer 3 (MP3) is fully supported.");
                    }

                    mp3Stream.Seek(0, SeekOrigin.Begin); // Reset stream for decoding

                    using (var wavStream = File.Create(wavPath))
                    {
                        int sampleRate = mpegFile.SampleRate;
                        int channels = mpegFile.Channels;
                        int bitsPerSample = 16;
                        int blockAlign = channels * bitsPerSample / 8;

                        Debug.WriteLine($"Initial format: SampleRate={sampleRate}, Channels={channels}, BlockAlign={blockAlign}");

                        // Write a placeholder WAV header (will update later)
                        WriteWavHeader(wavStream, channels, sampleRate, bitsPerSample, 0);

                        var floatBuffer = new float[1152 * channels];
                        var pcmBuffer = new byte[floatBuffer.Length * 2];
                        long totalBytes = 0;
                        long totalSamples = 0;
                        int frameCount = 0;

                        int samplesRead;
                        while ((samplesRead = mpegFile.ReadSamples(floatBuffer, 0, floatBuffer.Length)) > 0)
                        {
                            // Diagnostic: Log first 10 float samples of the first frame
                            if (frameCount == 0)
                            {
                                Debug.WriteLine("First 10 float samples:");
                                for (int i = 0; i < Math.Min(10, samplesRead); i++)
                                {
                                    Debug.WriteLine($"Sample[{i}]: {floatBuffer[i]}");
                                }
                            }
                            // Convert float samples to 16-bit PCM
                            for (int i = 0; i < samplesRead; i++)
                            {
                                short pcm = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, floatBuffer[i] * 32767f));
                                pcmBuffer[2 * i] = (byte)(pcm & 0xFF);
                                pcmBuffer[2 * i + 1] = (byte)((pcm >> 8) & 0xFF);
                            }
                            int bytesToWrite = samplesRead * 2;
                            wavStream.Write(pcmBuffer, 0, bytesToWrite);
                            totalBytes += bytesToWrite;
                            totalSamples += samplesRead;
                            frameCount++;
                            // Log frame info every 10 frames
                            if (frameCount % 10 == 0)
                            {
                                Debug.WriteLine($"Frame {frameCount}: {bytesToWrite} bytes, {totalSamples} samples, {totalBytes} bytes written.");
                            }
                        }

                        Debug.WriteLine($"Decoding complete. Total frames: {frameCount}, Total bytes: {totalBytes}, Total samples: {totalSamples}");
                        double durationSeconds = totalSamples / (double)sampleRate;
                        Debug.WriteLine($"Expected duration: {durationSeconds:F2} seconds");

                        // Compare decoded duration to MP3 duration
                        double mp3Duration = 0;
                        try
                        {
                            mp3Duration = mpegFile.Duration.TotalSeconds;
                            Debug.WriteLine($"Source MP3 duration: {mp3Duration:F2} seconds");
                            if (mp3Duration > 0 && Math.Abs(durationSeconds - mp3Duration) / mp3Duration > 0.1)
                            {
                                MessageBox.Show($"Warning: Decoded WAV duration ({durationSeconds:F2}s) is much shorter than source MP3 duration ({mp3Duration:F2}s). Output may be incomplete or corrupt. Consider using FFmpeg for this file.", "Duration Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                Debug.WriteLine($"Duration mismatch: WAV={durationSeconds:F2}s, MP3={mp3Duration:F2}s");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error reading MP3 duration: {ex}");
                        }

                        // Update WAV header with actual data length
                        wavStream.Seek(0, SeekOrigin.Begin);
                        WriteWavHeader(wavStream, channels, sampleRate, bitsPerSample, totalBytes);
                    }
                }

                MessageBox.Show($"WAV file created: {wavPath}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try
                {
                    var p = new Process
                    {
                        StartInfo = new ProcessStartInfo(wavPath)
                        {
                            UseShellExecute = true
                        }
                    };
                    p.Start();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error launching WAV file: {ex}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during MP3 decoding: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Debug.WriteLine($"Fatal error: {ex}");
            }
        }

        // Detect MPEG Layer from the first frame header
        private int GetMpegLayer(Stream mp3Stream)
        {
            long originalPos = mp3Stream.Position;
            mp3Stream.Seek(0, SeekOrigin.Begin);
            int layer = 0;
            try
            {
                // Read first 4 bytes (MPEG header)
                byte[] header = new byte[4];
                if (mp3Stream.Read(header, 0, 4) == 4)
                {
                    // Layer bits are bits 17-18 (from left, after sync)
                    // header[1] bits 1-2 (mask 0x06)
                    int layerBits = (header[1] & 0x06) >> 1;
                    switch (layerBits)
                    {
                        case 0b01: layer = 3; break; // Layer III
                        case 0b10: layer = 2; break; // Layer II
                        case 0b11: layer = 1; break; // Layer I
                        default: layer = 0; break;
                    }
                }
            }
            catch { layer = 0; }
            finally { mp3Stream.Seek(originalPos, SeekOrigin.Begin); }
            return layer;
        }

        // Writes a standard PCM WAV header to the stream
        private void WriteWavHeader(Stream stream, int channels, int sampleRate, int bitsPerSample, long dataLength)
        {
            try
            {
                var writer = new BinaryWriter(stream);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write((int)(36 + dataLength)); // ChunkSize
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16); // Subchunk1Size for PCM
                writer.Write((short)1); // AudioFormat PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * bitsPerSample / 8); // ByteRate
                writer.Write((short)(channels * bitsPerSample / 8)); // BlockAlign
                writer.Write((short)bitsPerSample);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write((int)dataLength); // Subchunk2Size
                Debug.WriteLine($"WAV header written: Channels={channels}, SampleRate={sampleRate}, BitsPerSample={bitsPerSample}, DataLength={dataLength}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error writing WAV header: {ex}");
                throw;
            }
        }
    }
}
