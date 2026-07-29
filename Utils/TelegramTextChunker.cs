namespace AdminConsole.Utils;

/// <summary>
/// Розбиває список готових текстових рядків на "сторінки" під ліміт
/// Telegram sendMessage/editMessageText (4096 символів). Використовуємо
/// запас (maxChars = 3500) — заголовок сторінки + emoji займають місце,
/// краще мати запас, ніж зловити BadRequest від API.
/// </summary>
public static class TelegramTextChunker
{
    private const int DefaultMaxChars = 3500;

    /// <summary>
    /// Об'єднує рядки в сторінки так, щоб жодна сторінка не перевищила
    /// maxChars. Один рядок ніколи не розбивається навпіл — якщо один
    /// рядок сам по собі довший за maxChars, він обрізається з "…".
    /// </summary>
    public static List<string> BuildPages(
        IReadOnlyList<string> lines,
        string                header    = "",
        int                   maxChars  = DefaultMaxChars)
    {
        var pages   = new List<string>();
        var current = new System.Text.StringBuilder(header);
        if (header.Length > 0) current.Append('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Length > maxChars
                ? rawLine[..(maxChars - 1)] + "…"
                : rawLine;

            // +1 за \n
            if (current.Length + line.Length + 1 > maxChars && current.Length > header.Length)
            {
                pages.Add(current.ToString().TrimEnd());
                current = new System.Text.StringBuilder(header);
                if (header.Length > 0) current.Append('\n');
            }

            current.Append(line).Append('\n');
        }

        pages.Add(current.ToString().TrimEnd());
        return pages.Count == 0 ? [header] : pages;
    }
}