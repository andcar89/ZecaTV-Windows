namespace ZecaTV.Models
{
    /// <summary>One entry in the category sidebar: an id (group title or a synthetic id), its display label, and how many channels it holds.</summary>
    public sealed record Category(string Id, string Label, int Count);
}
