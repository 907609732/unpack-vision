namespace UnpackVision.Core;

public enum IssueBarcodeAction
{
    None,
    AddTag,
    UndoLastTag
}

public sealed record IssueBarcodeMatch(IssueBarcodeAction Action, IssueTagDefinition? Tag = null);

public static class IssueTagBarcodeRouter
{
    public static IssueBarcodeMatch Match(string? rawValue, IEnumerable<IssueTagDefinition> tags)
    {
        var value = (rawValue ?? string.Empty).Trim();
        if (string.Equals(value, IssueTagDefaults.UndoBarcode, StringComparison.OrdinalIgnoreCase))
        {
            return new IssueBarcodeMatch(IssueBarcodeAction.UndoLastTag);
        }

        var tag = tags
            .Where(item => item.Enabled && !string.IsNullOrWhiteSpace(item.BarcodeValue))
            .FirstOrDefault(item => string.Equals(item.BarcodeValue, value, StringComparison.OrdinalIgnoreCase));
        return tag is null
            ? new IssueBarcodeMatch(IssueBarcodeAction.None)
            : new IssueBarcodeMatch(IssueBarcodeAction.AddTag, tag);
    }
}
