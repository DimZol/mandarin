using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Mandarin.Core;

/// <summary>Loads XML from untrusted files (EPUB, ODT, ODS): no DTD, no external entities, no network access.</summary>
public static class SafeXml
{
    static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = 200_000_000, // protection against huge XML bombs
        IgnoreComments = true
    };

    public static XDocument Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, Settings);
        return XDocument.Load(reader);
    }
}