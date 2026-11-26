using System.Xml;

namespace Niskala.Scanner.Fingerprints;

internal static class TaskXml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    public static IReadOnlyList<TaskTrace> ReadExecActions(string taskPath, string xml)
    {
        var actions = new List<TaskTrace>();
        try
        {
            using var text = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(text, Settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Exec"
                    && ReadExec(reader) is ({ } command, var arguments))
                {
                    actions.Add(new TaskTrace(taskPath, command, arguments));
                }
            }
        }
        catch (XmlException)
        {
            return actions;
        }

        return actions;
    }

    private static (string? Command, string? Arguments) ReadExec(XmlReader reader)
    {
        string? command = null;
        string? arguments = null;
        using XmlReader exec = reader.ReadSubtree();
        exec.Read();
        while (!exec.EOF)
        {
            if (exec.NodeType == XmlNodeType.Element && exec.LocalName is "Command" or "Arguments")
            {
                bool isCommand = exec.LocalName == "Command";
                string value = exec.ReadElementContentAsString().Trim();
                if (isCommand)
                {
                    command = value.Length == 0 ? null : value;
                }
                else
                {
                    arguments = value.Length == 0 ? null : value;
                }

                continue;
            }

            exec.Read();
        }

        return (command, arguments);
    }
}
