using System;
using System.IO;
using System.Linq;
using VsMcp.Extension.Tools;

var root = Path.Combine(Directory.GetCurrentDirectory(), "VsMcpSearchTests-" + Guid.NewGuid().ToString("N"));
try
{
    var solution = Path.Combine(root, "repo", "build");
    var project = Path.Combine(root, "repo", "src");
    var linked = Path.Combine(root, "shared", "linked.h");
    Directory.CreateDirectory(solution);
    Write(Path.Combine(project, "main.cpp"));
    Write(Path.Combine(project, "HEADER.H"));
    Write(Path.Combine(project, "nested", "other.cs"));
    Write(Path.Combine(project, "README"));
    Write(linked);
    foreach (var excluded in new[] { "bin", "OBJ", ".vs", "packages", "node_modules" })
        Write(Path.Combine(project, excluded, "ignored.cpp"));

    Check("EQ-1: a solution under build includes sibling project sources",
        new[] { solution, project }, Array.Empty<string>(), "*.cpp", "main.cpp");
    Check("EQ-2: semicolon patterns include each extension and ignore case",
        new[] { solution, project }, new[] { linked }, "*.h;*.cpp;*.cs",
        "HEADER.H", "main.cpp", "other.cs", "linked.h");
    Check("ER-1: overlapping roots, patterns, and linked files return each file once",
        new[] { project, Path.Combine(project, "nested"), project },
        new[] { Path.Combine(project, "main.cpp"), linked, linked }, "*.cpp;*.*;*.h",
        "HEADER.H", "main.cpp", "other.cs", "README", "linked.h");
    Check("BV-1: empty filter entries and surrounding whitespace are ignored",
        new[] { project }, Array.Empty<string>(), " ; *.h ;; *.cpp ; ", "HEADER.H", "main.cpp");
    Check("BV-2: empty pattern list defaults to all files, including extensionless files",
        new[] { project }, Array.Empty<string>(), " ; ", "HEADER.H", "main.cpp", "other.cs", "README");
    Check("ER-2: missing search directories do not hide other sources",
        new[] { Path.Combine(root, "missing"), project }, Array.Empty<string>(), "*.cs", "other.cs");
    Check("ER-3: explicitly listed build artifacts remain excluded",
        new[] { solution }, new[] { Path.Combine(project, "bin", "ignored.cpp"), linked }, "*.h;*.cpp", "linked.h");
    Check("EQ-3: unmatched patterns return no files",
        new[] { project }, new[] { linked }, "*.xaml");
    Check("EQ-4: question-mark wildcard matches one character",
        new[] { project }, Array.Empty<string>(), "mai?.cpp", "main.cpp");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, true);
}

void Write(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    File.WriteAllText(path, "Issue7SearchMarker");
}

void Check(string name, string[] directories, string[] files, string pattern, params string[] expected)
{
    var actual = SearchFileEnumerator.Enumerate(directories, files, pattern)
        .Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    var sortedExpected = expected.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    if (!actual.SequenceEqual(sortedExpected))
        throw new Exception(name + ": expected [" + string.Join(", ", sortedExpected) +
            "], got [" + string.Join(", ", actual) + "]");
    Console.WriteLine("PASS " + name);
}
