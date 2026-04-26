using System.Reflection.PortableExecutable;
using Iced.Intel;

namespace ManifestAnalyzer;

/// <summary>
/// Finds GameCore.Version::.cctor by enumerating every function in the PE via the
/// exception directory (.pdata), disassembling each, and locating the one whose body
/// writes a streak of 3+ consecutive byte immediates through a single base register —
/// the signature of a type static constructor initialising primitive byte fields.
///
/// Works on builds where Il2CppDumper cannot resolve CodeRegistration (older metadata
/// layouts, unusual IL2CPP linker configs) because it does not depend on metadata or
/// on Il2CppDumper's auto-search.
/// </summary>
internal static class PdataVersionCctorScanner
{
    private const int MaxFunctionBytes = 512;
    private const int MaxInstructionsPerFunction = 200;
    private const int MinStreakForCandidate = 3;
    private const int MinMajor = 1;
    private const int MaxMajor = 40;
    private const int MaxMinor = 200;
    private const int MaxRevision = 500;

    public static PdataCctorResult? TryFindVersion(byte[] gameAssemblyData, string logPrefix)
    {
        var functionRanges = EnumerateFunctionRanges(gameAssemblyData);
        if (functionRanges.Count == 0)
        {
            Console.WriteLine($"{logPrefix} pdata scanner: no .pdata exception records found; cannot enumerate functions");
            return null;
        }

        var candidates = new List<Candidate>();

        foreach (var (beginRva, endRva) in functionRanges)
        {
            var functionSize = endRva - beginRva;
            if (functionSize is < 8 or > 2048)
                continue;

            if (!TryRvaToFileOffset(gameAssemblyData, beginRva, out var fileOffset))
                continue;

            var disassembleLength = (int)Math.Min((uint)MaxFunctionBytes, functionSize);
            if (fileOffset + disassembleLength > gameAssemblyData.Length)
                continue;

            var streak = FindByteStoreStreak(gameAssemblyData, fileOffset, disassembleLength);
            if (streak == null || streak.Writes.Count < MinStreakForCandidate)
                continue;

            var major = streak.Writes[0].Value;
            var minor = streak.Writes[1].Value;
            var revision = streak.Writes[2].Value;
            if (major < MinMajor || major > MaxMajor || minor > MaxMinor)
                continue;
            _ = revision; // bounded by byte range; revision cap is informational only

            var score = ScoreCandidate(streak);
            candidates.Add(new Candidate(beginRva, score, streak));
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine($"{logPrefix} pdata scanner: no functions matched the byte-store cctor pattern");
            return null;
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = candidates[0];
        var runnerUp = candidates.Count > 1 ? candidates[1] : null;

        var bestTriple = (best.Streak.Writes[0].Value, best.Streak.Writes[1].Value, best.Streak.Writes[2].Value);
        var scoreGap = runnerUp == null ? int.MaxValue : best.Score - runnerUp.Score;

        Console.WriteLine(
            $"{logPrefix} pdata scanner: best candidate at RVA 0x{best.Rva:X} score={best.Score} streak_len={best.Streak.Writes.Count} -> {bestTriple.Item1}.{bestTriple.Item2}.{bestTriple.Item3}" +
            (runnerUp != null ? $"  (runner-up score={runnerUp.Score})" : ""));

        if (scoreGap < 20)
        {
            Console.WriteLine(
                $"{logPrefix} pdata scanner: confidence is low (gap to runner-up = {scoreGap}). Rejecting to avoid a false positive.");
            return null;
        }

        return new PdataCctorResult(
            Major: bestTriple.Item1,
            Minor: bestTriple.Item2,
            Revision: bestTriple.Item3,
            FunctionRva: best.Rva,
            StreakLength: best.Streak.Writes.Count,
            Score: best.Score,
            RunnerUpScore: runnerUp?.Score);
    }

    private static List<(uint Begin, uint End)> EnumerateFunctionRanges(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var exceptionDir = peReader.PEHeaders.PEHeader?.ExceptionTableDirectory;
            if (exceptionDir is null || exceptionDir.Value.Size == 0)
                return [];

            var section = peReader.PEHeaders.GetContainingSectionIndex(exceptionDir.Value.RelativeVirtualAddress);
            if (section < 0)
                return [];

            var sectionHeader = peReader.PEHeaders.SectionHeaders[section];
            var rawOffset = sectionHeader.PointerToRawData + (exceptionDir.Value.RelativeVirtualAddress - sectionHeader.VirtualAddress);
            var sizeBytes = exceptionDir.Value.Size;
            var entryCount = sizeBytes / 12;

            var result = new List<(uint Begin, uint End)>(entryCount);
            for (var i = 0; i < entryCount; i++)
            {
                var entryOffset = rawOffset + i * 12;
                if (entryOffset + 12 > data.Length)
                    break;
                var begin = BitConverter.ToUInt32(data, entryOffset);
                var end = BitConverter.ToUInt32(data, entryOffset + 4);
                if (begin == 0 || end <= begin)
                    continue;
                result.Add((begin, end));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private static bool TryRvaToFileOffset(byte[] data, uint rva, out int fileOffset)
    {
        fileOffset = 0;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            foreach (var section in peReader.PEHeaders.SectionHeaders)
            {
                var sectionStart = (uint)section.VirtualAddress;
                var sectionEnd = sectionStart + (uint)section.VirtualSize;
                if (rva >= sectionStart && rva < sectionEnd)
                {
                    fileOffset = section.PointerToRawData + (int)(rva - sectionStart);
                    return fileOffset >= 0 && fileOffset < data.Length;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static ByteStoreStreak? FindByteStoreStreak(byte[] data, int fileOffset, int length)
    {
        var codeReader = new ByteArrayCodeReader(data, fileOffset, length);
        var decoder = Decoder.Create(64, codeReader);
        decoder.IP = 0;

        var byWriteBaseReg = new Dictionary<Register, List<(int Disp, byte Value)>>();
        var instructionCount = 0;

        while (codeReader.CanReadByte && instructionCount < MaxInstructionsPerFunction)
        {
            decoder.Decode(out var instruction);
            if (instruction.IsInvalid)
                break;
            instructionCount++;

            if (instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch)
                break;

            if (instruction.Mnemonic != Mnemonic.Mov)
                continue;

            if (instruction.Op0Kind != OpKind.Memory || instruction.Op1Kind != OpKind.Immediate8)
                continue;

            if (instruction.MemorySize != MemorySize.UInt8)
                continue;

            var baseReg = instruction.MemoryBase;
            if (baseReg == Register.None)
                continue;

            var disp = (int)instruction.MemoryDisplacement32;
            var value = (byte)instruction.Immediate8;

            if (!byWriteBaseReg.TryGetValue(baseReg, out var list))
                byWriteBaseReg[baseReg] = list = new List<(int, byte)>();
            list.Add((disp, value));
        }

        // For each base register's writes, find the longest near-consecutive streak starting at a small offset.
        ByteStoreStreak? best = null;
        var bestScoreLocal = -1;

        foreach (var (reg, writes) in byWriteBaseReg)
        {
            writes.Sort((a, b) => a.Disp.CompareTo(b.Disp));

            for (var startIdx = 0; startIdx < writes.Count; startIdx++)
            {
                var startOffset = writes[startIdx].Disp;
                if (startOffset is < 0 or > 64)
                    continue;

                var streak = new List<ByteWrite> { new(writes[startIdx].Disp, writes[startIdx].Value) };
                var cur = startOffset;
                for (var j = startIdx + 1; j < writes.Count; j++)
                {
                    var gap = writes[j].Disp - cur;
                    if (gap is >= 1 and <= 4)
                    {
                        streak.Add(new ByteWrite(writes[j].Disp, writes[j].Value));
                        cur = writes[j].Disp;
                    }
                    else if (gap == 0)
                    {
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }

                if (streak.Count < MinStreakForCandidate)
                    continue;

                var candidate = new ByteStoreStreak(reg, streak);
                var score = ScoreCandidate(candidate);
                if (score > bestScoreLocal)
                {
                    best = candidate;
                    bestScoreLocal = score;
                }
            }
        }

        return best;
    }

    private static int ScoreCandidate(ByteStoreStreak streak)
    {
        var score = streak.Writes.Count * 10;
        if (streak.Writes[0].Offset == 0)
            score += 20;
        if (streak.Writes.Count >= 6)
            score += 30;
        return score;
    }

    private sealed record ByteWrite(int Offset, byte Value);
    private sealed record ByteStoreStreak(Register BaseRegister, List<ByteWrite> Writes);
    private sealed record Candidate(uint Rva, int Score, ByteStoreStreak Streak);
}

internal sealed record PdataCctorResult(
    byte Major,
    byte Minor,
    byte Revision,
    uint FunctionRva,
    int StreakLength,
    int Score,
    int? RunnerUpScore);
