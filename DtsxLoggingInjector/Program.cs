using System.Text;
using System.Xml;
using System.Xml.Linq;

var app = new DtsxLoggingInjector();
return app.Run(args);

internal sealed class DtsxLoggingInjector
{
    private static readonly HashSet<string> LoggingEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnError",
        "OnPreExecute",
        "OnPostExecute"
    };

    public int Run(string[] args)
    {
        try
        {
            var options = ParseArguments(args);

            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            var sourcePath = options.SourcePath ?? throw new InvalidOperationException("Brak ścieżki do pliku wzorcowego.");
            var targetPath = options.TargetPath ?? throw new InvalidOperationException("Brak ścieżki do pliku docelowego.");
            var dryRun = options.DryRun;

            if (!File.Exists(sourcePath))
            {
                Console.Error.WriteLine($"Nie znaleziono pliku wzorcowego: {sourcePath}");
                PrintUsage();
                return 1;
            }

            if (!File.Exists(targetPath))
            {
                Console.Error.WriteLine($"Nie znaleziono pliku docelowego: {targetPath}");
                PrintUsage();
                return 1;
            }

            var sourceDoc = LoadDtsxDocument(sourcePath);
            var targetDoc = LoadDtsxDocument(targetPath);

            var inserted = ApplyLoggingPattern(sourceDoc, targetDoc);

            if (inserted == 0)
            {
                Console.WriteLine("Brak brakujących elementów wzorca do wstawienia.");
            }
            else if (!dryRun)
            {
                targetDoc.Save(targetPath);
                Console.WriteLine($"Dodano {inserted} elementów wzorca do pliku: {targetPath}");
            }
            else
            {
                Console.WriteLine($"[dry-run] Dodatkowo można dodać {inserted} elementów wzorca do: {targetPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Błąd: {ex.Message}");
            return 1;
        }
    }

    private static int ApplyLoggingPattern(XDocument sourceDoc, XDocument targetDoc)
    {
        var dtsNs = XNamespace.Get("www.microsoft.com/SqlServer/Dts");

        var sourceRoot = sourceDoc.Root ?? throw new InvalidOperationException("Pakiet źródłowy nie posiada elementu głównego.");
        var targetRoot = targetDoc.Root ?? throw new InvalidOperationException("Pakiet docelowy nie posiada elementu głównego.");

        var sourceVariables = sourceRoot.Element(dtsNs + "Variables")?.Elements(dtsNs + "Variable")
            .Where(variable => ((string?)variable.Attribute(dtsNs + "ObjectName"))?.StartsWith("vLog", StringComparison.OrdinalIgnoreCase) == true)
            .ToList() ?? new List<XElement>();
        var injectedCount = 0;

        if (sourceVariables.Count > 0)
        {
            var targetVariables = targetRoot.Element(dtsNs + "Variables");
            if (targetVariables is null)
            {
                targetVariables = new XElement(dtsNs + "Variables");
                targetRoot.AddFirst(targetVariables);
            }

            foreach (var sourceVariable in sourceVariables)
            {
                var sourceName = (string?)sourceVariable.Attribute(dtsNs + "ObjectName");
                var sourceNamespace = (string?)sourceVariable.Attribute(dtsNs + "Namespace");
                var alreadyPresent = targetVariables.Elements(dtsNs + "Variable").Any(variable =>
                    string.Equals((string?)variable.Attribute(dtsNs + "ObjectName"), sourceName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string?)variable.Attribute(dtsNs + "Namespace"), sourceNamespace, StringComparison.OrdinalIgnoreCase));

                if (!alreadyPresent)
                {
                    targetVariables.Add(CloneWithFreshGuids(sourceVariable));
                    injectedCount++;
                }
            }
        }

        var sourceHandlers = sourceRoot
            .Elements(dtsNs + "EventHandlers")
            .Elements(dtsNs + "EventHandler")
            .Where(IsLoggingEvent)
            .ToList();

        if (sourceHandlers.Count == 0)
        {
            throw new InvalidOperationException("Pakiet wzorcowy nie zawiera event handlerów z logowaniem.");
        }

        var targetEventHandlersRoot = targetRoot.Element(dtsNs + "EventHandlers");
        if (targetEventHandlersRoot is null)
        {
            targetRoot.Add(new XElement(dtsNs + "EventHandlers"));
            targetEventHandlersRoot = targetRoot.Element(dtsNs + "EventHandlers") ?? throw new InvalidOperationException("Nie można utworzyć sekcji EventHandlers w pakiecie docelowym.");
        }

        var targetHandlers = targetEventHandlersRoot.Elements(dtsNs + "EventHandler").ToList();
        foreach (var sourceHandler in sourceHandlers)
        {
            var eventName = (string)sourceHandler.Attribute(dtsNs + "EventName");
            if (string.IsNullOrWhiteSpace(eventName))
            {
                continue;
            }

            ValidateConnectionReferences(sourceHandler, sourceRoot, targetRoot);

            var existing = targetHandlers.FirstOrDefault(h =>
                string.Equals((string)h.Attribute(dtsNs + "EventName"), eventName, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var clonedHandler = CloneWithMappedConnections(sourceHandler, sourceRoot, targetRoot);
                targetEventHandlersRoot.Add(clonedHandler);
                injectedCount++;
                targetHandlers = targetEventHandlersRoot.Elements(dtsNs + "EventHandler").ToList();
                continue;
            }

            var sourceExecutables = sourceHandler.Element(dtsNs + "Executables")?.Elements(dtsNs + "Executable") ?? Enumerable.Empty<XElement>();
            var targetExecutables = existing.Element(dtsNs + "Executables");
            if (targetExecutables is null)
            {
                existing.Add(new XElement(dtsNs + "Executables"));
                targetExecutables = existing.Element(dtsNs + "Executables") ?? throw new InvalidOperationException("Nie można utworzyć sekcji Executables dla event handlera.");
            }

            foreach (var sourceExecutable in sourceExecutables)
            {
                var sourceObjectName = (string)sourceExecutable.Attribute(dtsNs + "ObjectName");
                if (string.IsNullOrWhiteSpace(sourceObjectName))
                {
                    continue;
                }

                var alreadyPresent = targetExecutables.Elements(dtsNs + "Executable")
                    .Any(x => string.Equals((string)x.Attribute(dtsNs + "ObjectName"), sourceObjectName, StringComparison.OrdinalIgnoreCase));

                if (!alreadyPresent)
                {
                    var clonedExecutable = CloneWithMappedConnections(sourceExecutable, sourceRoot, targetRoot);
                    targetExecutables.Add(clonedExecutable);
                    injectedCount++;
                }
            }
        }

        return injectedCount;
    }

    private static XElement CloneWithMappedConnections(XElement source, XElement sourceRoot, XElement targetRoot)
    {
        var clone = CloneWithFreshGuids(source);

        foreach (var attribute in clone.DescendantsAndSelf().Attributes().Where(IsConnectionManagerReference))
        {
            var reference = attribute.Value;
            var (sourceManager, targetManager) = ResolveConnectionManagers(reference, sourceRoot, targetRoot);
            var dtsNs = XNamespace.Get("www.microsoft.com/SqlServer/Dts");
            if (attribute.Name.LocalName.Equals("connectionManagerRefId", StringComparison.OrdinalIgnoreCase))
            {
                attribute.Value = (string?)targetManager.Attribute(dtsNs + "refId")
                    ?? throw new InvalidOperationException($"Connection manager '{(string?)sourceManager.Attribute(dtsNs + "ObjectName")}' w pakiecie docelowym nie ma atrybutu DTS:refId.");
            }
            else if (attribute.Name.LocalName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string?)sourceManager.Attribute(dtsNs + "DTSID"), reference, StringComparison.OrdinalIgnoreCase))
            {
                attribute.Value = (string?)targetManager.Attribute(dtsNs + "DTSID")
                    ?? throw new InvalidOperationException($"Connection manager '{(string?)sourceManager.Attribute(dtsNs + "ObjectName")}' w pakiecie docelowym nie ma atrybutu DTS:DTSID.");
            }
            else
            {
                attribute.Value = (string?)targetManager.Attribute(dtsNs + "refId")
                    ?? throw new InvalidOperationException($"Connection manager '{(string?)sourceManager.Attribute(dtsNs + "ObjectName")}' w pakiecie docelowym nie ma atrybutu DTS:refId.");
            }
        }

        return clone;
    }

    private static void ValidateConnectionReferences(XElement source, XElement sourceRoot, XElement targetRoot)
    {
        foreach (var attribute in source.DescendantsAndSelf().Attributes().Where(IsConnectionManagerReference))
        {
            _ = ResolveConnectionManagers(attribute.Value, sourceRoot, targetRoot);
        }
    }

    private static (XElement SourceManager, XElement TargetManager) ResolveConnectionManagers(string reference, XElement sourceRoot, XElement targetRoot)
    {
        var dtsNs = XNamespace.Get("www.microsoft.com/SqlServer/Dts");
        var sourceManagers = sourceRoot.Element(dtsNs + "ConnectionManagers")?.Elements(dtsNs + "ConnectionManager") ?? Enumerable.Empty<XElement>();
        var targetManagers = targetRoot.Element(dtsNs + "ConnectionManagers")?.Elements(dtsNs + "ConnectionManager") ?? Enumerable.Empty<XElement>();
        var matchingSourceManagers = sourceManagers.Where(manager =>
            string.Equals((string?)manager.Attribute(dtsNs + "refId"), reference, StringComparison.OrdinalIgnoreCase)
            || string.Equals((string?)manager.Attribute(dtsNs + "DTSID"), reference, StringComparison.OrdinalIgnoreCase)
            || string.Equals((string?)manager.Attribute(dtsNs + "ObjectName"), reference, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matchingSourceManagers.Count != 1)
        {
            throw new InvalidOperationException($"Nie można jednoznacznie odnaleźć connection managera źródłowego dla referencji '{reference}'.");
        }

        var sourceManager = matchingSourceManagers[0];
        var sourceName = (string?)sourceManager.Attribute(dtsNs + "ObjectName");
        var matchingTargetManagers = targetManagers.Where(manager =>
            string.Equals((string?)manager.Attribute(dtsNs + "ObjectName"), sourceName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matchingTargetManagers.Count != 1)
        {
            throw new InvalidOperationException(matchingTargetManagers.Count == 0
                ? $"Pakiet docelowy nie zawiera connection managera '{sourceName}' wymaganego przez event handler."
                : $"Pakiet docelowy zawiera więcej niż jeden connection manager o nazwie '{sourceName}'.");
        }

        return (sourceManager, matchingTargetManagers[0]);
    }

    private static bool IsConnectionManagerReference(XAttribute attribute)
    {
        return attribute.Name.LocalName.Equals("connectionManagerID", StringComparison.OrdinalIgnoreCase)
            || attribute.Name.LocalName.Equals("connectionManagerRefId", StringComparison.OrdinalIgnoreCase)
            || attribute.Name.LocalName.Equals("Connection", StringComparison.OrdinalIgnoreCase);
    }

    private static XElement CloneWithFreshGuids(XElement source)
    {
        var clone = new XElement(source);

        foreach (var element in clone.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.Name.LocalName == "DTSID")
                {
                    attribute.SetValue($"{{{Guid.NewGuid():D}}}");
                }
            }
        }

        return clone;
    }

    private static XDocument LoadDtsxDocument(string path)
    {
        var bytes = File.ReadAllBytes(path);

        foreach (var encoding in GetCandidateEncodings())
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                // Próba z innym kodowaniem lub oczyszczeniem tekstu.
            }

            try
            {
                var text = encoding.GetString(bytes);
                var cleaned = RemoveInvalidXmlChars(text);
                return XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                // Kolejna próba z innym kodowaniem.
            }
        }

        throw new InvalidOperationException($"Nie udało się poprawnie odczytać pliku DTSX: {path}");
    }

    private static IEnumerable<Encoding> GetCandidateEncodings()
    {
        yield return Encoding.UTF8;
        yield return Encoding.GetEncoding(1250);
        yield return Encoding.GetEncoding(1252);
        yield return Encoding.Unicode;
        yield return Encoding.BigEndianUnicode;
        yield return Encoding.Default;
    }

    private static string RemoveInvalidXmlChars(string text)
    {
        var builder = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsLegalXmlChar(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsLegalXmlChar(int ch)
    {
        return ch == 0x9 || ch == 0xA || ch == 0xD || (ch >= 0x20 && ch <= 0xD7FF) || (ch >= 0xE000 && ch <= 0xFFFD) || (ch >= 0x10000 && ch <= 0x10FFFF);
    }

    private static bool IsLoggingEvent(XElement eventHandler)
    {
        var dtsNs = XNamespace.Get("www.microsoft.com/SqlServer/Dts");
        var eventName = (string)eventHandler.Attribute(dtsNs + "EventName");
        return !string.IsNullOrWhiteSpace(eventName) && LoggingEvents.Contains(eventName);
    }

    private static Arguments ParseArguments(string[] args)
    {
        var options = new Arguments();
        var index = 0;

        while (index < args.Length)
        {
            var arg = args[index];

            switch (arg)
            {
                case "--source":
                    index++;
                    options.SourcePath = GetRequiredValue(args, ref index, "--source");
                    break;

                case "--target":
                    index++;
                    options.TargetPath = GetRequiredValue(args, ref index, "--target");
                    break;

                case "--dry-run":
                    options.DryRun = true;
                    index++;
                    break;

                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    index++;
                    break;

                default:
                    if (options.SourcePath is null)
                    {
                        options.SourcePath = arg;
                    }
                    else if (options.TargetPath is null)
                    {
                        options.TargetPath = arg;
                    }
                    else
                    {
                        throw new ArgumentException($"Nieznany parametr: {arg}");
                    }

                    index++;
                    break;
            }
        }

        var defaultBase = Directory.GetCurrentDirectory();
        var defaultSource = ResolveDefaultFile(defaultBase, "Package.dtsx", "Integration Services Project1/Package.dtsx");
        var defaultTarget = ResolveDefaultFile(defaultBase, "PackageWithoutLogging.dtsx", "Integration Services Project1/PackageWithoutLogging.dtsx");

        options.SourcePath ??= defaultSource;
        options.TargetPath ??= defaultTarget;

        return options;
    }

    private static string GetRequiredValue(string[] args, ref int index, string parameterName)
    {
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"Brak wartości po parametrze {parameterName}.");
        }

        var value = args[index];
        index++;
        return value;
    }

    private static string ResolveDefaultFile(string baseDirectory, string fileName, string relativeCandidate)
    {
        var candidates = new[]
        {
            Path.Combine(baseDirectory, fileName),
            Path.Combine(baseDirectory, relativeCandidate.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(baseDirectory, "..", fileName),
            Path.Combine(baseDirectory, "..", relativeCandidate.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(baseDirectory, "Integration Services Project1", fileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(baseDirectory, fileName);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("DtsxLoggingInjector");
        Console.WriteLine("Użycie:");
        Console.WriteLine("  dotnet run --project DtsxLoggingInjector -- [--source wzorzec.dtsx] [--target docelowy.dtsx] [--dry-run]");
        Console.WriteLine();
        Console.WriteLine("Domyślnie program szuka:");
        Console.WriteLine("  - Package.dtsx jako wzorzec");
        Console.WriteLine("  - PackageWithoutLogging.dtsx jako pakiet docelowy");
    }

    private sealed class Arguments
    {
        public string? SourcePath { get; set; }
        public string? TargetPath { get; set; }
        public bool DryRun { get; set; }
        public bool ShowHelp { get; set; }
    }
}
