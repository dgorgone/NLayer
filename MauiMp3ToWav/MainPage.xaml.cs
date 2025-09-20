using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Microsoft.Maui.ApplicationModel;
using NLayer;

namespace MauiMp3ToWav
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
        }

        private async void OnConvertClicked(object? sender, EventArgs e)
        {
            try
            {
                FileResult? result = null;
                try
                {
                    result = await FilePicker.PickAsync(new PickOptions
                    {
                        PickerTitle = "Select an MP3 file to decode",
                        FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                        {
                            { DevicePlatform.iOS, new[] { "public.mp3" } },
                            { DevicePlatform.Android, new[] { "audio/mpeg" } },
                            { DevicePlatform.WinUI, new[] { ".mp3" } },
                            { DevicePlatform.MacCatalyst, new[] { "public.mp3" } }
                        })
                    });
                }
                catch (Exception ex)
                {
                    await DisplayAlert("File Picker Error", $"Error opening file picker: {ex.Message}", "OK");
                    return;
                }

                if (result == null)
                {
                    await DisplayAlert("Cancelled", "File selection was cancelled.", "OK");
                    return;
                }

                string mp3Path = result.FullPath;
                string wavFileName = Path.GetFileNameWithoutExtension(mp3Path) + ".wav";
                string wavPath = Path.Combine(FileSystem.AppDataDirectory, wavFileName);

                Console.WriteLine($"Input MP3: {mp3Path}");
                Console.WriteLine($"Output WAV: {wavPath}");

                using (var mp3Stream = File.OpenRead(mp3Path))
                using (var mpegFile = new MpegFile(mp3Stream))
                {
                    // MPEG Layer check (Layer 3 is MP3)
                    int mpegLayer = GetMpegLayer(mp3Stream);
                    if (mpegLayer != 3)
                    {
                        await DisplayAlert("Warning", $"Warning: This file is MPEG Layer {mpegLayer}. Only Layer 3 (MP3) is fully supported. Output may be corrupt.", "OK");
                        Console.WriteLine($"Warning: Detected MPEG Layer {mpegLayer}. Only Layer 3 (MP3) is fully supported.");
                    }

                    mp3Stream.Seek(0, SeekOrigin.Begin); // Reset stream for decoding

                    using (var wavStream = File.Create(wavPath))
                    {
                        int sampleRate = mpegFile.SampleRate;
                        int channels = mpegFile.Channels;
                        int bitsPerSample = 16;
                        int blockAlign = channels * bitsPerSample / 8;

                        Console.WriteLine($"Initial format: SampleRate={sampleRate}, Channels={channels}, BlockAlign={blockAlign}");

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
                                Console.WriteLine("First 10 float samples:");
                                for (int i = 0; i < Math.Min(10, samplesRead); i++)
                                {
                                    Console.WriteLine($"Sample[{i}]: {floatBuffer[i]}");
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
                                Console.WriteLine($"Frame {frameCount}: {bytesToWrite} bytes, {totalSamples} samples, {totalBytes} bytes written.");
                            }
                        }

                        Console.WriteLine($"Decoding complete. Total frames: {frameCount}, Total bytes: {totalBytes}, Total samples: {totalSamples}");
                        double durationSeconds = totalSamples / (double)sampleRate;
                        Console.WriteLine($"Expected duration: {durationSeconds:F2} seconds");

                        // Compare decoded duration to MP3 duration
                        double mp3Duration = 0;
                        try
                        {
                            mp3Duration = mpegFile.Duration.TotalSeconds;
                            Console.WriteLine($"Source MP3 duration: {mp3Duration:F2} seconds");
                            if (mp3Duration > 0 && Math.Abs(durationSeconds - mp3Duration) / mp3Duration > 0.1)
                            {
                                await DisplayAlert("Duration Mismatch", $"Warning: Decoded WAV duration ({durationSeconds:F2}s) is much shorter than source MP3 duration ({mp3Duration:F2}s). Output may be incomplete or corrupt. Consider using FFmpeg for this file.", "OK");
                                Console.WriteLine($"Duration mismatch: WAV={durationSeconds:F2}s, MP3={mp3Duration:F2}s");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error reading MP3 duration: {ex}");
                        }

                        // Update WAV header with actual data length
                        wavStream.Seek(0, SeekOrigin.Begin);
                        WriteWavHeader(wavStream, channels, sampleRate, bitsPerSample, totalBytes);
                    }
                }

                await DisplayAlert("Success", $"WAV file created: {wavPath}", "OK");
                try
                {
                    await Launcher.OpenAsync(new OpenFileRequest
                    {
                        File = new ReadOnlyFile(wavPath)
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error launching WAV file: {ex}");
                    await DisplayAlert("Error", "Could not open the WAV file.", "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Error during MP3 decoding: {ex.Message}", "OK");
                Console.WriteLine($"Fatal error: {ex}");
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
                Console.WriteLine($"WAV header written: Channels={channels}, SampleRate={sampleRate}, BitsPerSample={bitsPerSample}, DataLength={dataLength}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing WAV header: {ex}");
                throw;
            }
        }
    }
}
