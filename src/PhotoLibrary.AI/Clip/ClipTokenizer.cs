using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoLibrary.AI.Clip;

/// <summary>
/// The byte-level BPE tokenizer used by OpenAI CLIP, ported from clip/simple_tokenizer.py. Produces the
/// 77-token context the text encoder expects: start token, BPE ids, end token, then padding.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int ContextLength = 77;
    public const int StartOfText = 49406;
    public const int EndOfText = 49407;

    private readonly Dictionary<string, int> _encoder;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly Dictionary<byte, char> _byteToChar;
    private readonly Dictionary<string, string[]> _cache = new();

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|\p{L}+|\p{N}|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public ClipTokenizer(string vocabPath, string mergesPath)
    {
        using var vocab = JsonDocument.Parse(File.ReadAllText(vocabPath, Encoding.UTF8));
        _encoder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in vocab.RootElement.EnumerateObject()) _encoder[p.Name] = p.Value.GetInt32();

        _ranks = new Dictionary<(string, string), int>();
        var rank = 0;
        foreach (var line in File.ReadLines(mergesPath, Encoding.UTF8))
        {
            if (line.StartsWith("#version", StringComparison.Ordinal) || line.Length == 0) continue;
            var parts = line.Split(' ');
            if (parts.Length != 2) continue;
            _ranks.TryAdd((parts[0], parts[1]), rank++);
        }

        _byteToChar = BytesToUnicode();
    }

    /// <summary>GPT-2 style mapping of every byte to a printable character so the BPE vocabulary is plain text.</summary>
    private static Dictionary<byte, char> BytesToUnicode()
    {
        var bs = new List<int>();
        for (var b = (int)'!'; b <= '~'; b++) bs.Add(b);
        for (var b = 0xA1; b <= 0xAC; b++) bs.Add(b);
        for (var b = 0xAE; b <= 0xFF; b++) bs.Add(b);
        var cs = new List<int>(bs);
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (bs.Contains(b)) continue;
            bs.Add(b);
            cs.Add(256 + n++);
        }
        var map = new Dictionary<byte, char>();
        for (var i = 0; i < bs.Count; i++) map[(byte)bs[i]] = (char)cs[i];
        return map;
    }

    /// <summary>BPE ids for the text, without start/end tokens.</summary>
    public List<int> EncodeTokens(string text)
    {
        text = Whitespace().Replace(text, " ").Trim().ToLowerInvariant();
        var ids = new List<int>();
        foreach (Match m in TokenPattern().Matches(text))
        {
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(m.Value)) sb.Append(_byteToChar[b]);
            foreach (var piece in Bpe(sb.ToString()))
                if (_encoder.TryGetValue(piece, out var id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Fixed-length (77) input ids and attention mask for the text encoder.</summary>
    public (long[] InputIds, long[] AttentionMask) Encode(string text)
    {
        var tokens = EncodeTokens(text);
        if (tokens.Count > ContextLength - 2) tokens = tokens.Take(ContextLength - 2).ToList();
        var ids = new long[ContextLength];
        var mask = new long[ContextLength];
        ids[0] = StartOfText;
        mask[0] = 1;
        for (var i = 0; i < tokens.Count; i++) { ids[i + 1] = tokens[i]; mask[i + 1] = 1; }
        ids[tokens.Count + 1] = EndOfText;
        mask[tokens.Count + 1] = 1;
        for (var i = tokens.Count + 2; i < ContextLength; i++) ids[i] = EndOfText; // HF pads with the end token
        return (ids, mask);
    }

    private string[] Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var cached)) return cached;
        if (token.Length == 0) return [];

        var word = new List<string>();
        for (var i = 0; i < token.Length - 1; i++) word.Add(token[i].ToString());
        word.Add(token[^1] + "</w>");

        while (word.Count > 1)
        {
            (string, string)? best = null;
            var bestRank = int.MaxValue;
            for (var i = 0; i < word.Count - 1; i++)
            {
                if (_ranks.TryGetValue((word[i], word[i + 1]), out var r) && r < bestRank) { bestRank = r; best = (word[i], word[i + 1]); }
            }
            if (best is null) break;
            var (first, second) = best.Value;
            var merged = new List<string>();
            for (var i = 0; i < word.Count; i++)
            {
                if (i < word.Count - 1 && word[i] == first && word[i + 1] == second) { merged.Add(first + second); i++; }
                else merged.Add(word[i]);
            }
            word = merged;
        }

        var result = word.ToArray();
        _cache[token] = result;
        return result;
    }
}
