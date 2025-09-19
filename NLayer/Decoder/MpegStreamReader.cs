using System;
using System.IO;
using System.Diagnostics;

namespace NLayer.Decoder
{
    class MpegStreamReader
    {
        ID3Frame _id3Frame, _id3v1Frame;
        RiffHeaderFrame _riffHeaderFrame;

        VBRInfo _vbrInfo;
        MpegFrame _first, _current, _last, _lastFree;

        long _readOffset, _eofOffset;
        Stream _source;
        bool _canSeek, _endFound, _mixedFrameSize;
        object _readLock = new object();
        object _frameLock = new object();

        internal MpegStreamReader(Stream source)
        {
            _source = source;
            _canSeek = source.CanSeek;
            _readOffset = 0L;
            _eofOffset = long.MaxValue;

            // find the first Mpeg frame (may skip leading ID3 / RIFF)
            var frame = FindNextFrame();
            while (frame != null && !(frame is MpegFrame))
            {
                frame = FindNextFrame();
            }
            if (frame == null) throw new InvalidDataException("Not a valid MPEG file!");

            // find the second frame (must also be MPEG)
            frame = FindNextFrame();
            if (frame == null || !(frame is MpegFrame)) throw new InvalidDataException("Not a valid MPEG file!");

            _current = _first;
        }

        FrameBase FindNextFrame()
        {
            if (_endFound) return null;

            var freeFrame = _lastFree;
            var lastFrameStart = _readOffset;

            lock (_frameLock)
            {
                var syncBuf = new byte[4];
                try
                {
                    if (Read(_readOffset, syncBuf, 0, 4) == 4)
                    {
                        do
                        {
                            var sync = (uint)(syncBuf[0] << 24 | syncBuf[1] << 16 | syncBuf[2] << 8 | syncBuf[3]);
                            lastFrameStart = _readOffset;

                            // ID3v2
                            if (_id3Frame == null)
                            {
                                var f = ID3Frame.TrySync(sync);
                                if (f != null && f.Validate(_readOffset, this))
                                {
                                    if (!_canSeek) f.SaveBuffer();
                                    _readOffset += f.Length;
                                    DiscardThrough(_readOffset, true);
#if DEBUG
                                    Debug.WriteLine($"[NLayer] ID3v2 @0x{(_readOffset - f.Length):X} len={f.Length}");
#endif
                                    return _id3Frame = f;
                                }
                            }

                            // RIFF header (rare wrapper)
                            if (_first == null && _riffHeaderFrame == null)
                            {
                                var f = RiffHeaderFrame.TrySync(sync);
                                if (f != null && f.Validate(_readOffset, this))
                                {
                                    _readOffset += f.Length;
                                    DiscardThrough(_readOffset, true);
#if DEBUG
                                    Debug.WriteLine($"[NLayer] RIFF header @0x{(_readOffset - f.Length):X} len={f.Length}");
#endif
                                    return _riffHeaderFrame = f;
                                }
                            }

                            // MPEG frame attempt
                            var candidate = MpegFrame.TrySync(sync);
                            if (candidate != null)
                            {
                                bool reject = false;
                                string rejectReason = string.Empty;

                                // free-format guard check (relaxed) – only reject if core format differs
                                if (freeFrame != null && (candidate.Layer != freeFrame.Layer || candidate.Version != freeFrame.Version || candidate.SampleRate != freeFrame.SampleRate))
                                {
                                    reject = true;
                                    rejectReason = "format mismatch after free-format";
                                }

                                // validate header/content
                                if (!reject)
                                {
                                    if (!candidate.Validate(_readOffset, this))
                                    {
                                        reject = true;
                                        rejectReason = "Validate failed";
                                    }
                                }

                                if (!reject)
                                {
                                    if (!_canSeek)
                                    {
                                        candidate.SaveBuffer();
                                        DiscardThrough(_readOffset + candidate.FrameLength, true);
                                    }

                                    _readOffset += candidate.FrameLength;

                                    if (_first == null)
                                    {
                                        if (_vbrInfo == null && (_vbrInfo = candidate.ParseVBR()) != null)
                                        {
#if DEBUG
                                            Debug.WriteLine("[NLayer] VBR header parsed – continuing to real first frame");
#endif
                                            return FindNextFrame();
                                        }
                                        candidate.Number = 0;
                                        _first = _last = candidate;
                                    }
                                    else
                                    {
                                        if (candidate.SampleCount != _first.SampleCount) _mixedFrameSize = true;
                                        candidate.SampleOffset = _last.SampleCount + _last.SampleOffset;
                                        candidate.Number = _last.Number + 1;
                                        _last = (_last.Next = candidate);
                                    }

                                    if (candidate.BitRateIndex == 0)
                                    {
                                        _lastFree = candidate; // entering / staying in free-format
                                    }
                                    else if (freeFrame != null && freeFrame.BitRateIndex == 0 && candidate.BitRateIndex > 0)
                                    {
                                        _lastFree = null; // leaving free-format hypothesis
                                    }
#if DEBUG
                                    Debug.WriteLine($"[NLayer] Frame #{candidate.Number} off=0x{candidate.Offset:X} len={candidate.FrameLength} sc={candidate.SampleCount} sr={candidate.SampleRate} ch={candidate.Channels} brIdx={candidate.BitRateIndex} br={candidate.BitRate}");
#endif
                                    return candidate;
                                }
                                else
                                {
#if DEBUG
                                    Debug.WriteLine($"[NLayer][REJECT] off=0x{_readOffset:X} hdr={syncBuf[0]:X2}{syncBuf[1]:X2}{syncBuf[2]:X2}{syncBuf[3]:X2} reason={rejectReason}");
#endif
                                }
                            }

                            // Possible mid-stream ID3 tag (after some frames)
                            if (_last != null)
                            {
                                var f2 = ID3Frame.TrySync(sync);
                                if (f2 != null && f2.Validate(_readOffset, this))
                                {
                                    if (!_canSeek) f2.SaveBuffer();
                                    if (f2.Version == 1) _id3v1Frame = f2; else _id3Frame.Merge(f2);
                                    _readOffset += f2.Length;
                                    DiscardThrough(_readOffset, true);
#if DEBUG
                                    Debug.WriteLine($"[NLayer] ID3 tag mid-stream @0x{(_readOffset - f2.Length):X} len={f2.Length}");
#endif
                                    return f2;
                                }
                            }

                            // advance one byte and try to resync
                            ++_readOffset;
                            if (_first == null || !_canSeek) DiscardThrough(_readOffset, true);
                            Buffer.BlockCopy(syncBuf, 1, syncBuf, 0, 3);
                        } while (Read(_readOffset + 3, syncBuf, 3, 1) == 1);
                    }

                    lastFrameStart += 4; // include last 4 bytes for free-format bookkeeping
                    _endFound = true;
#if DEBUG
                    long fileLen = (_canSeek ? SafeGetLength(_source) : -1);
                    long lastEnd = (_last != null ? (_last.Offset + _last.FrameLength) : 0);
                    if (fileLen > 0 && lastEnd < fileLen)
                    {
                        Debug.WriteLine($"[NLayer][SCAN-END] Premature end? lastFrameEnd=0x{lastEnd:X} fileLen=0x{fileLen:X} remaining={fileLen - lastEnd} bytes");
                    }
                    else
                    {
                        Debug.WriteLine($"[NLayer][SCAN-END] lastFrameEnd=0x{lastEnd:X} fileLen=0x{fileLen:X}");
                    }
#endif
                    return null;
                }
                finally
                {
                    if (freeFrame != null)
                    {
                        freeFrame.Length = (int)(lastFrameStart - freeFrame.Offset);
                        if (!_canSeek) throw new InvalidOperationException("Free frames cannot be read properly from forward-only streams!");
                        if (_lastFree == freeFrame) _lastFree = null;
                    }
                }
            }
        }

        static long SafeGetLength(Stream s)
        {
            try { return s.Length; } catch { return -1; }
        }

        class ReadBuffer
        {
            public byte[] Data;
            public long BaseOffset;
            public int End;
            public int DiscardCount;
            object _localLock = new object();
            public ReadBuffer(int initialSize) { initialSize = 2 << (int)Math.Log(initialSize, 2); Data = new byte[initialSize]; }
            public int Read(MpegStreamReader reader, long offset, byte[] buffer, int index, int count)
            {
                lock (_localLock)
                {
                    var startIdx = EnsureFilled(reader, offset, ref count);
                    Buffer.BlockCopy(Data, startIdx, buffer, index, count);
                }
                return count;
            }
            public int ReadByte(MpegStreamReader reader, long offset)
            {
                lock (_localLock)
                {
                    var count = 1; var startIdx = EnsureFilled(reader, offset, ref count); if (count == 1) return Data[startIdx];
                }
                return -1;
            }
            int EnsureFilled(MpegStreamReader reader, long offset, ref int count)
            {
                var startIdx = (int)(offset - BaseOffset); int endIdx = startIdx + count;
                if (startIdx < 0 || endIdx > End)
                {
                    int readStart = 0, readCount = 0, moveCount = 0; long readOffset = 0;
                    if (startIdx < 0)
                    {
                        if (!reader._source.CanSeek) throw new InvalidOperationException("Cannot seek backwards on a forward-only stream!");
                        if (End > 0) { if ((startIdx + Data.Length > 0) || (Data.Length * 2 <= 16384 && startIdx + Data.Length * 2 > 0)) { endIdx = End; } }
                        readOffset = offset;
                        if (endIdx < 0) { Truncate(); BaseOffset = offset; startIdx = 0; endIdx = count; readCount = count; }
                        else { moveCount = -endIdx; readCount = -startIdx; }
                    }
                    else
                    {
                        if (endIdx < Data.Length) { readCount = endIdx - End; readStart = End; readOffset = BaseOffset + readStart; }
                        else if (endIdx - DiscardCount < Data.Length) { moveCount = DiscardCount; readStart = End; readCount = endIdx - readStart; readOffset = BaseOffset + readStart; }
                        else if (Data.Length * 2 <= 16384) { moveCount = DiscardCount; readStart = End; readCount = endIdx - End; readOffset = BaseOffset + readStart; }
                        else { Truncate(); BaseOffset = offset; readOffset = offset; startIdx = 0; endIdx = count; readCount = count; }
                    }
                    if (endIdx - moveCount > Data.Length || readStart + readCount - moveCount > Data.Length)
                    {
                        var newSize = Data.Length * 2; while (newSize < endIdx - moveCount) newSize *= 2; var newBuf = new byte[newSize];
                        if (moveCount < 0) { Buffer.BlockCopy(Data, 0, newBuf, -moveCount, End + moveCount); DiscardCount = 0; }
                        else { Buffer.BlockCopy(Data, moveCount, newBuf, 0, End - moveCount); DiscardCount -= moveCount; }
                        Data = newBuf;
                    }
                    else if (moveCount != 0)
                    {
                        if (moveCount > 0) { Buffer.BlockCopy(Data, moveCount, Data, 0, End - moveCount); DiscardCount -= moveCount; }
                        else { for (int i = 0, srcIdx = Data.Length - 1, destIdx = Data.Length - 1 - moveCount; i < moveCount; i++, srcIdx--, destIdx--) Data[destIdx] = Data[srcIdx]; DiscardCount = 0; }
                    }
                    BaseOffset += moveCount; readStart -= moveCount; startIdx -= moveCount; endIdx -= moveCount; End -= moveCount;
                    lock (reader._readLock)
                    {
                        if (readCount > 0 && reader._source.Position != readOffset && readOffset < reader._eofOffset)
                        {
                            if (reader._canSeek) { try { reader._source.Position = readOffset; } catch (EndOfStreamException) { reader._eofOffset = reader._source.Length; readCount = 0; } }
                            else { var seekCount = readOffset - reader._source.Position; while (--seekCount >= 0) { if (reader._source.ReadByte() == -1) { reader._eofOffset = reader._source.Position; readCount = 0; break; } } }
                        }
                        while (readCount > 0 && readOffset < reader._eofOffset)
                        {
                            var temp = reader._source.Read(Data, readStart, readCount); if (temp == 0) break; readStart += temp; readOffset += temp; readCount -= temp;
                        }
                        if (readStart > End) End = readStart;
                        if (End < endIdx) { count = Math.Max(0, End - startIdx); }
                        else if (End < Data.Length) { var temp = reader._source.Read(Data, End, Data.Length - End); End += temp; }
                    }
                }
                return startIdx;
            }
            public void DiscardThrough(long offset)
            {
                lock (_localLock)
                {
                    var count = (int)(offset - BaseOffset); DiscardCount = Math.Max(count, DiscardCount); if (DiscardCount >= Data.Length) CommitDiscard();
                }
            }
            void Truncate() { End = 0; DiscardCount = 0; }
            void CommitDiscard()
            {
                if (DiscardCount >= Data.Length || DiscardCount >= End) { BaseOffset += DiscardCount; End = 0; }
                else { Buffer.BlockCopy(Data, DiscardCount, Data, 0, End - DiscardCount); BaseOffset += DiscardCount; End -= DiscardCount; }
                DiscardCount = 0;
            }
        }

        // increased initial buffer to reduce fragmentation with many small frames
        ReadBuffer _readBuf = new ReadBuffer(8192);

        internal int Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0L) throw new ArgumentOutOfRangeException("offset");
            if (index < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException("index");
            return _readBuf.Read(this, offset, buffer, index, count);
        }
        internal int ReadByte(long offset)
        {
            if (offset < 0L) throw new ArgumentOutOfRangeException("offset");
            return _readBuf.ReadByte(this, offset);
        }
        internal void DiscardThrough(long offset, bool minimalRead) { _readBuf.DiscardThrough(offset); }

        internal void ReadToEnd()
        {
            try
            {
                var maxAllocation = 40000; if (_id3Frame != null) maxAllocation += _id3Frame.Length;
                while (!_endFound)
                {
                    FindNextFrame();
                    while (!_canSeek && FrameBase.TotalAllocation >= maxAllocation)
                    {
#if NET35
                        System.Threading.Thread.Sleep(500);
#else
                        System.Threading.Tasks.Task.Delay(500).Wait();
#endif
                    }
                }
            }
            catch (ObjectDisposedException) { }
        }

        internal bool CanSeek { get { return _canSeek; } }
        internal long SampleCount { get { if (_vbrInfo != null) return _vbrInfo.VBRStreamSampleCount; if (!_canSeek) return -1; ReadToEnd(); return _last.SampleCount + _last.SampleOffset; } }
        internal int SampleRate { get { if (_vbrInfo != null) return _vbrInfo.SampleRate; return _first.SampleRate; } }
        internal int Channels { get { if (_vbrInfo != null) return _vbrInfo.Channels; return _first.Channels; } }
        internal int FirstFrameSampleCount { get { return (_first != null ? _first.SampleCount : 0); } }

        internal long SeekTo(long sampleNumber)
        {
            if (!_canSeek) throw new InvalidOperationException("Cannot seek!");
            var cnt = (int)(sampleNumber / _first.SampleCount); var frame = _first;
            if (_current != null && _current.Number <= cnt && _current.SampleOffset <= sampleNumber) { frame = _current; cnt -= frame.Number; }
            while (!_mixedFrameSize && --cnt >= 0 && frame != null)
            {
                if (frame == _last && !_endFound) { do { FindNextFrame(); } while (frame == _last && !_endFound); }
                if (_mixedFrameSize) break; frame = frame.Next;
            }
            while (frame != null && frame.SampleOffset + frame.SampleCount < sampleNumber)
            {
                if (frame == _last && !_endFound) { do { FindNextFrame(); } while (frame == _last && !_endFound); }
                frame = frame.Next;
            }
            if (frame == null) return -1; return (_current = frame).SampleOffset;
        }

        internal MpegFrame NextFrame()
        {
            var frame = _current; if (frame != null)
            {
                if (_canSeek) { frame.SaveBuffer(); DiscardThrough(frame.Offset + frame.FrameLength, false); }
                if (frame == _last && !_endFound) { do { FindNextFrame(); } while (frame == _last && !_endFound); }
                _current = frame.Next;
                if (!_canSeek)
                {
                    lock (_frameLock) { var temp = _first; _first = temp.Next; temp.Next = null; }
                }
            }
            return frame;
        }
        internal MpegFrame GetCurrentFrame() { return _current; }
    }
}
