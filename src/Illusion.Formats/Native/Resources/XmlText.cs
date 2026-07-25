using System.Text;
using System.Xml;
using System.Xml.XPath;

namespace Illusion.Formats.Native.Resources;

/// <summary>
/// The managed half of the XML entry pipeline: rendering a natively decoded node graph to the
/// .xml text modders edit, and parsing that text back into the graph for the native encoder.
/// Deliberately reproduces the pre-port twins' behavior verbatim — same XmlWriter settings, same
/// value filtering, same culture-dependent number handling — so the emitted text and the packed
/// bytes stay identical to the managed reference.
/// </summary>
internal static class XmlText
{
    private const byte KindAbsent = 0;
    private const byte KindSpecial = 1;
    private const byte KindBoolean = 2;
    private const byte KindFloat = 3;
    private const byte KindString = 4;
    private const byte KindInteger = 5;
    private const byte KindUnknown = 8;

    // ── rendering (decode side) ──

    internal static string RenderCodec0(List<Model.XmlNodeEntry> nodes)
    {
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true };
        var output = new StringBuilder();
        using XmlWriter writer = XmlWriter.Create(output, settings);
        writer.WriteStartDocument();

        if (nodes.Count > 0)
        {
            Model.XmlNodeEntry root = SingleById(nodes, 0);
            if (root.Children.Count != 1 || root.Attributes.Count > 0
                || Filter(root.Value) is not null)
            {
                throw new FormatException("the codec-0 root node is not a pure single-child root");
            }
            foreach (uint childId in root.Children)
            {
                WriteNode0(writer, nodes, SingleById(nodes, childId));
            }
        }

        writer.WriteEndDocument();
        writer.Flush();
        return output.ToString();
    }

    private static void WriteNode0(XmlWriter writer, List<Model.XmlNodeEntry> nodes, Model.XmlNodeEntry node)
    {
        object? value = Filter(node.Value);
        if (value is not null && node.Value.Kind == KindSpecial
            && value.ToString()!.Contains("--", StringComparison.Ordinal))
        {
            return; // the managed renderer drops special values that would break a comment
        }

        object name = Filter(node.Name)
            ?? throw new FormatException("a codec-0 node carries no usable name");
        writer.WriteStartElement(name.ToString()!);

        foreach (Model.XmlAttributeEntry attribute in node.Attributes)
        {
            object attrName = Filter(attribute.Name)
                ?? throw new FormatException("a codec-0 attribute carries no usable name");
            writer.WriteStartAttribute(attrName.ToString()!);
            writer.WriteValue(Filter(attribute.Value)?.ToString() ?? "");
            writer.WriteEndAttribute();
        }

        foreach (uint childId in node.Children)
        {
            WriteNode0(writer, nodes, SingleById(nodes, childId));
        }

        if (value is not null)
        {
            if (node.Value.Kind != KindString)
            {
                writer.WriteAttributeString("__type", TypeLetter(node.Value.Kind));
            }
            writer.WriteValue(value.ToString());
        }
        writer.WriteEndElement();
    }

    internal static string RenderCodec1(List<Model.XmlNodeEntry> nodes)
    {
        if (nodes.Count == 0)
        {
            throw new FormatException("the codec-1 document has no root node");
        }
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true };
        var output = new StringBuilder();
        using XmlWriter writer = XmlWriter.Create(output, settings);
        writer.WriteStartDocument();
        WriteNode1(writer, nodes, nodes[0]);
        writer.WriteEndDocument();
        writer.Flush();
        return output.ToString();
    }

    private static void WriteNode1(XmlWriter writer, List<Model.XmlNodeEntry> nodes, Model.XmlNodeEntry node)
    {
        writer.WriteStartElement(node.Name.StringValue);
        foreach (Model.XmlAttributeEntry attribute in node.Attributes)
        {
            writer.WriteStartAttribute(attribute.Name.StringValue);
            writer.WriteValue(attribute.Value.Kind == KindAbsent ? "" : attribute.Value.StringValue);
            writer.WriteEndAttribute();
        }
        foreach (uint childId in node.Children)
        {
            WriteNode1(writer, nodes, SingleById(nodes, childId));
        }
        if (node.Value.Kind != KindAbsent)
        {
            writer.WriteValue(node.Value.StringValue);
        }
        writer.WriteEndElement();
    }

    // ── parsing (encode side; content is a file path, like the managed twins take) ──

    internal static List<Model.XmlNodeEntry> ParseCodec0(string path)
    {
        var nodes = new List<Model.XmlNodeEntry>
        {
            new() { Id = 0 }, // the synthetic root (no name, no value)
        };

        var document = new XPathDocument(path);
        XPathNavigator nav = document.CreateNavigator();
        nav.MoveToRoot();
        XPathNodeIterator children = nav.SelectChildren(XPathNodeType.Element);
        if (children.Count != 1 || !children.MoveNext())
        {
            throw new InvalidOperationException("the .xml file must have exactly one root element");
        }
        ReadNode0(nodes, nodes[0], children.Current!);
        return nodes;
    }

    private static void ReadNode0(List<Model.XmlNodeEntry> nodes, Model.XmlNodeEntry parent, XPathNavigator nav)
    {
        var node = new Model.XmlNodeEntry
        {
            Name = new Model.XmlValue { Kind = KindString, StringValue = nav.Name },
            Id = (uint)nodes.Count,
        };
        parent.Children.Add(node.Id);
        nodes.Add(node);

        byte type = KindString;
        if (nav.MoveToFirstAttribute())
        {
            do
            {
                if (nav.Name == "__type")
                {
                    type = TypeFromLetter(nav.Value);
                    continue;
                }
                node.Attributes.Add(new Model.XmlAttributeEntry
                {
                    Name = new Model.XmlValue { Kind = KindString, StringValue = nav.Name },
                    Value = new Model.XmlValue { Kind = KindString, StringValue = nav.Value },
                });
            }
            while (nav.MoveToNextAttribute());
            nav.MoveToParent();
        }

        XPathNodeIterator children = nav.SelectChildren(XPathNodeType.Element);
        if (children.Count > 0)
        {
            while (children.MoveNext())
            {
                ReadNode0(nodes, node, children.Current!);
            }
        }
        else if (!string.IsNullOrEmpty(nav.Value))
        {
            // The managed twin parses typed values at serialization time with
            // current-culture semantics; that parsing stays on this side.
            node.Value = type switch
            {
                KindBoolean => new Model.XmlValue { Kind = type, BoolValue = (byte)(bool.Parse(nav.Value) ? 1 : 0) },
                KindFloat => new Model.XmlValue { Kind = type, FloatValue = float.Parse(nav.Value) },
                KindInteger => new Model.XmlValue { Kind = type, IntValue = int.Parse(nav.Value) },
                KindUnknown => new Model.XmlValue { Kind = type, FloatValue = Convert.ToSingle(nav.Value) },
                _ => new Model.XmlValue { Kind = type, StringValue = nav.Value },
            };
        }
    }

    internal static List<Model.XmlNodeEntry> ParseCodec1(string path)
    {
        var document = new XmlDocument();
        document.LoadXml(File.ReadAllText(path));

        XmlElement? root = document.DocumentElement;
        if (root is null)
        {
            throw new InvalidOperationException("the .xml file must have exactly one root element");
        }

        var nodes = new List<Model.XmlNodeEntry>();
        ReadNode1(nodes, root);
        return nodes;
    }

    private static uint ReadNode1(List<Model.XmlNodeEntry> nodes, XmlElement element)
    {
        uint id = (uint)nodes.Count;
        nodes.Add(new Model.XmlNodeEntry
        {
            Name = new Model.XmlValue { Kind = KindString, StringValue = element.Name },
            Id = id,
        });

        foreach (XmlNode child in element.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Element)
            {
                uint childId = ReadNode1(nodes, (XmlElement)child);
                nodes[(int)id].Children.Add(childId);
            }
            else if (child.NodeType == XmlNodeType.Text && child.Value is not null)
            {
                if (element.ChildNodes.Count != 1)
                {
                    // The binary format cannot express text mixed with elements — the
                    // managed writer would emit a payload its own reader refuses.
                    throw new FormatException("codec-1 XML cannot carry text mixed with elements");
                }
                nodes[(int)id].Value = new Model.XmlValue { Kind = KindString, StringValue = child.Value };
            }
        }

        foreach (XmlAttribute attribute in element.Attributes)
        {
            nodes[(int)id].Attributes.Add(new Model.XmlAttributeEntry
            {
                Name = new Model.XmlValue { Kind = KindString, StringValue = attribute.Name },
                Value = new Model.XmlValue { Kind = KindString, StringValue = attribute.Value },
            });
        }

        return id;
    }

    // ── shared ──

    /// <summary>The managed reader's value filtering: empty strings and comment-breaking
    /// content decode to null. Returns the .NET-typed value (so ToString matches the twins).</summary>
    private static object? Filter(Model.XmlValue value) => value.Kind switch
    {
        KindAbsent => null,
        KindSpecial when string.IsNullOrEmpty(value.StringValue)
            || value.StringValue.Contains("<!--", StringComparison.Ordinal)
            || value.StringValue.Contains("\n\t >", StringComparison.Ordinal)
            || value.StringValue.Contains('\t', StringComparison.Ordinal) => null,
        KindSpecial => value.StringValue,
        KindString when string.IsNullOrEmpty(value.StringValue)
            || value.StringValue.Contains("<!--", StringComparison.Ordinal) => null,
        KindString => value.StringValue,
        KindBoolean => value.BoolValue != 0,
        KindFloat => value.FloatValue,
        KindInteger => value.IntValue,
        KindUnknown => value.FloatValue,
        _ => throw new FormatException($"unknown XML value kind {value.Kind}"),
    };

    private static Model.XmlNodeEntry SingleById(List<Model.XmlNodeEntry> nodes, uint id)
    {
        Model.XmlNodeEntry? found = null;
        foreach (Model.XmlNodeEntry node in nodes)
        {
            if (node.Id != id)
            {
                continue;
            }
            if (found is not null)
            {
                throw new InvalidOperationException($"node id {id} appears more than once");
            }
            found = node;
        }
        return found ?? throw new KeyNotFoundException($"node id {id} is missing");
    }

    private static string TypeLetter(byte kind) => kind switch
    {
        KindSpecial => "x",
        KindBoolean => "b",
        KindFloat => "f",
        KindString => "s",
        KindInteger => "i",
        KindUnknown => "u",
        _ => throw new NotSupportedException($"XML value kind {kind}"),
    };

    private static byte TypeFromLetter(string letter) => letter switch
    {
        "x" => KindSpecial,
        "b" => KindBoolean,
        "f" => KindFloat,
        "s" => KindString,
        "i" => KindInteger,
        "u" => KindUnknown,
        _ => throw new NotSupportedException($"XML __type '{letter}'"),
    };
}
