using System;
using System.Xml;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

#nullable enable

namespace EngUpdater
{

    public class VersionDetails {
        public string Name = String.Empty;
        public string Version = String.Empty;
        public string Uri = String.Empty;
        public string Sha = String.Empty;
        public string CoherentParentDependency = String.Empty;
    }

    class VersionUpdater
    {
        public static readonly string RoslynPackagePropertyName = "MicrosoftNetCompilersPackageVersion";
        public static readonly string MSBuildPyRefRegexString = "revision *= *'([0-9a-fA-F]*)'";
        public static readonly string NuGetPyVersionRegexString = "version *= *'([^']*)'";

        public static async Task<Dictionary<string,VersionDetails>> ReadVersionDetails (Stream stream)
        {
            var versions = new Dictionary<string,VersionDetails> ();
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings () { Async = true }))
            {
                while (await reader.ReadAsync()) {
                    switch (reader.NodeType) {
                    case XmlNodeType.Element when reader.Name == "Dependency":
                        var details = reader.ReadDependency ();
                        versions [details.Name] = details;
                        //Console.WriteLine ($"{details.Name} ==> {details.Version}");
                        break;
                    case XmlNodeType.Element when reader.Name == "ProductDependencies":
                    case XmlNodeType.Element when reader.Name == "Dependencies":
                        break;
                    case XmlNodeType.Element:
                        await reader.SkipAsync ();
                        break;
                    default:
                        break;
                    }
                }
                return versions;
            }
        }

        public static async Task<Dictionary<string,string>> ReadProps (Stream stream, bool verbose)
        {
            using (XmlReader reader = XmlReader.Create (stream, new XmlReaderSettings () { Async = true }))
            {
                var settings = new Dictionary<string,string>();
                bool inGroup = false;
                while (await reader.ReadAsync ()) {
                    switch (reader.NodeType) {
                    case XmlNodeType.Element when reader.Name == "PropertyGroup":
                        inGroup = true;
                        break;
                    case XmlNodeType.Element when reader.Name == "Project":
                        break;
                    case XmlNodeType.Element when inGroup:
                        var key = reader.Name;
                        await reader.ReadAsync ();
                        var value = await reader.GetValueAsync ();
                        if (key.Contains ("Version") && Char.IsDigit (value[0])) {
                            var packageKey = !key.EndsWith ("PackageVersion") ? key.Replace ("Version", "PackageVersion") : key;
                            settings[packageKey] = value;
                            if (verbose) Console.WriteLine ($"{packageKey} => {value}");
                        }
                        break;
                    case XmlNodeType.Element:
                        await reader.SkipAsync ();
                        break;
                    case XmlNodeType.EndElement when reader.Name == "PropertyGroup":
                        inGroup = false;
                        break;
                    default:
                        break;
                    }
                }
                return settings;
            }
        }

        public static async Task<Dictionary<string,string>> UpdateProps (Stream inputStream, Stream outputStream, Dictionary<string,string> versions, bool stripPackage = false)
        {
            XmlWriterSettings settings = new XmlWriterSettings ();
            settings.Encoding = new UTF8Encoding (false);
            settings.Indent = true;
            var updatedValues = new Dictionary<string,string> ();

            using (var reader = XmlReader.Create (inputStream, new XmlReaderSettings { Async = true })) {
                using (var writer = outputStream != null ? XmlWriter.Create (outputStream, settings) : XmlWriter.Create (Console.Out)) {
                    while (await reader.ReadAsync ()) {
                        var name = stripPackage ? reader.Name.Replace ("Version", "PackageVersion") : reader.Name;
                        if (reader.NodeType == XmlNodeType.Element && versions.TryGetValue (name, out var value)) {
                            var oldValue = writer.WriteUpdatedElementString (reader, value, true);
                            if (value != oldValue)
                                updatedValues [name] = value;
                        } else {
                            writer.WriteNode (reader);
                        }
                    }
                }
            }

            return updatedValues;
        }

        public static async Task<Dictionary<string,VersionDetails>> UpdateDetails (Stream inputStream, Stream outputStream, Dictionary<string,VersionDetails> versions)
        {
            XmlWriterSettings settings = new XmlWriterSettings ();
            settings.Indent = true;
            settings.Encoding = new UTF8Encoding (false);
            var updatedDetails = new Dictionary<String,VersionDetails> ();

            using (var reader = XmlReader.Create (inputStream, new XmlReaderSettings { Async = true })) {
                using (var writer = outputStream != null ? XmlWriter.Create (outputStream, settings) : XmlWriter.Create (Console.Out)) {
                    while (await reader.ReadAsync ()) {
                        if (reader.NodeType == XmlNodeType.Element && reader.Name == "Dependency") {
                            var name = reader.GetAttribute ("Name");
                            if (versions.TryGetValue (name, out var details)) {
                                var oldDetails = writer.WriteDependency (reader, details);
                                if (details.Version != oldDetails.Version)
                                    updatedDetails [name] = oldDetails;
                            } else {
                                writer.WriteNode (reader);
                            }
                        } else {
                            writer.WriteNode (reader);
                        }
                    }
                }
            }

            return updatedDetails;
        }
    }

    public static class XmlIOExtensions {
        public static string WriteUpdatedElementString (this XmlWriter writer, XmlReader reader, string value, bool checkDigit = false)
        {
            writer.WriteNode (reader);
            reader.Read ();
            var oldValue = reader.Value;

            if (!checkDigit || Char.IsDigit (oldValue[0])) {
                writer.WriteString (value);
            } else {
                writer.WriteNode (reader);
            }

            reader.Read ();
            writer.WriteNode (reader);

            return oldValue;
        }

        public static VersionDetails ReadDependency (this XmlReader reader)
        {
            var details = new VersionDetails ();
            
            while (reader.MoveToNextAttribute ()) {
                ReadDetails (reader, details);
            }

            while (reader.Read ()) {
                switch (reader.NodeType) {
                case XmlNodeType.Element:
                    ReadDetails (reader, details);
                    break;
                case XmlNodeType.EndElement when reader.Name == "Dependency":
                    return details;
                }
            }

            void ReadDetails (XmlReader read, VersionDetails detail) 
            {
                var name = read.Name;
                if (read.NodeType == XmlNodeType.Element)
                    read.Read ();

                switch (name) {
                case "Name":
                    detail.Name = read.Value;
                    break;
                case nameof (VersionDetails.Version):
                    detail.Version = read.Value;
                    break;
                case nameof (VersionDetails.Uri):
                    detail.Uri = read.Value;
                    break;
                case nameof (VersionDetails.Sha):
                    detail.Sha = read.Value;
                    break;
                case nameof (VersionDetails.CoherentParentDependency):
                    detail.CoherentParentDependency = read.Value;
                    break;
                default:
                    read.Skip ();
                    break;
                }
            }

            return details;
        }

        public static VersionDetails WriteDependency (this XmlWriter writer, XmlReader reader, VersionDetails details)
        {
            var oldDetails = new VersionDetails ();
            writer.WriteStartElement (reader.Prefix, reader.LocalName, reader.NamespaceURI);
            writer.WriteAttributeString ("Name", reader.NamespaceURI, details.Name);
            writer.WriteAttributeString ("Version", reader.NamespaceURI, details.Version);

            oldDetails.Name = reader.GetAttribute ("Name");
            oldDetails.Version = reader.GetAttribute ("Version");
            if (details.CoherentParentDependency != null)
                writer.WriteAttributeString ("CoherentParentDependency", reader.NamespaceURI, details.CoherentParentDependency);

            bool wroteUri = false, wroteSha = false;
            while (reader.Read()) {
                switch (reader.NodeType) {
                case XmlNodeType.Element:
                    if (reader.Name == "Uri") {
                        oldDetails.Uri = writer.WriteUpdatedElementString (reader, details.Uri);
                        wroteUri = true;
                    } else if (reader.Name == "Sha") {
                        oldDetails.Sha = writer.WriteUpdatedElementString (reader, details.Sha);
                        wroteSha = true;
                    } else {
                        writer.WriteNode (reader);
                    }
                    break;
                case XmlNodeType.EndElement:
                    writer.WriteNode (reader);
                    if (reader.Name == "Dependency") {
                        if (!(wroteSha && wroteUri)) {
                            throw new Exception ("Something was missed");
                        }
                    
                        return oldDetails;
                    }
                    break;
                default:
                    writer.WriteNode (reader);
                    break;
                }
            }
            return oldDetails;
        }

        public static void WriteNode (this XmlWriter writer, XmlReader reader)
        {
            switch (reader.NodeType) {
            case XmlNodeType.Element:
                writer.WriteStartElement (reader.Prefix, reader.LocalName, reader.NamespaceURI);
                writer.WriteAttributes (reader, true );

                if (reader.IsEmptyElement)
                    writer.WriteEndElement ();

                break;
            case XmlNodeType.EndElement:
                writer.WriteFullEndElement ();
                break;
            case XmlNodeType.Text:
                writer.WriteString (reader.Value);
                break;
            case XmlNodeType.Whitespace:
            case XmlNodeType.SignificantWhitespace:
                writer.WriteWhitespace (reader.Value);
                break;
            case XmlNodeType.CDATA:
                writer.WriteCData (reader.Value);
                break;
            case XmlNodeType.EntityReference:
                writer.WriteEntityRef (reader.Name);
                break;
            case XmlNodeType.XmlDeclaration:
            case XmlNodeType.ProcessingInstruction:
                writer.WriteProcessingInstruction (reader.Name, reader.Value);
                break;
            case XmlNodeType.DocumentType:
                writer.WriteDocType (reader.Name, reader.GetAttribute ("PUBLIC"), reader.GetAttribute ("SYSTEM"), reader.Value);
                break;
            case XmlNodeType.Comment:
                writer.WriteComment (reader.Value);
                break;
            }
        }
    }

}
