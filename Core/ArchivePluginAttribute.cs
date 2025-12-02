using System;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ArchivePluginAttribute : Attribute
{
    public string Id { get; }
    public string[] Extensions { get; }
    public string[] Magics { get; }

    public ArchivePluginAttribute(string id, string[]? extensions = null, string[]? magics = null)
    {
        Id = id;
        Extensions = extensions ?? Array.Empty<string>();
        Magics = magics ?? Array.Empty<string>();
    }
}