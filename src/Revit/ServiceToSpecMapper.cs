using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PcfExport.Revit
{
    /// <summary>
    /// Helpers for the Service-to-Spec mapping tool:
    ///  • ExtensibleStorage persistence on ProjectInformation
    ///  • Shared parameter creation (UserModifiable = false)
    ///  • Applying the Pipe Spec value to a FabricationPart
    /// </summary>
    internal static class ServiceToSpecMapper
    {
        // Older builds wrote "<spec><systemTypeName>" into the value.
        // We strip everything from this character onward when loading so
        // mappings saved during the System Type experiment keep working as
        // spec-only.
        private const char LegacyValueSeparator = '';

        // ── ExtensibleStorage identifiers ────────────────────────────────────────
        private static readonly System.Guid SchemaGuid =
            new System.Guid("22D348A8-B6B9-4A09-9E7C-E3D87398D29B");
        private const string SchemaName = "PcfExport_ServiceToSpec";
        private const string FieldName  = "ServiceSpecJson";

        // ── Shared parameter identifiers ─────────────────────────────────────────
        public const string PipeSpecParamName = "Pipe Spec";
        private const string SharedParamGroup = "PcfExport";

        // ─────────────────────────────────────────────────────────────────────────
        // ExtensibleStorage — load / save mappings
        // ─────────────────────────────────────────────────────────────────────────

        public static Dictionary<string, string> LoadMappings(Document doc)
        {
            var result = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                var schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return result;

                var pi = doc.ProjectInformation;
                var entity = pi.GetEntity(schema);
                if (!entity.IsValid()) return result;

                string json = entity.Get<string>(schema.GetField(FieldName));
                if (string.IsNullOrWhiteSpace(json)) return result;

                json = json.Trim();
                if (json.Length < 2 || json[0] != '{') return result;
                json = json[1..^1];

                foreach (var pair in SplitJsonPairs(json))
                {
                    if (pair.Key == null || pair.Value == null) continue;

                    // Strip the legacy "<systemTypeName>" suffix.
                    string spec = pair.Value;
                    int sepIx = spec.IndexOf(LegacyValueSeparator);
                    if (sepIx >= 0) spec = spec.Substring(0, sepIx);

                    result[pair.Key] = spec;
                }
            }
            catch { /* return empty on any error */ }
            return result;
        }

        public static void SaveMappings(Document doc, Dictionary<string, string> mappings)
        {
            var schema = GetOrCreateSchema();
            var pi     = doc.ProjectInformation;
            var entity = new Entity(schema);

            var pairs = mappings
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => $"\"{EscapeJson(kv.Key)}\":\"{EscapeJson(kv.Value)}\"");
            string json = "{" + string.Join(",", pairs) + "}";

            entity.Set(schema.GetField(FieldName), json);
            pi.SetEntity(entity);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Fabrication service discovery
        // ─────────────────────────────────────────────────────────────────────────

        public static List<string> GetFabricationServices(Document doc)
        {
            var services = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var elem in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .WhereElementIsNotElementType()
                .Cast<FabricationPart>())
            {
                string? svc = elem.ServiceName;
                if (!string.IsNullOrWhiteSpace(svc))
                    services.Add(svc);
            }

            return services.OrderBy(s => s).ToList();
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Shared parameter creation
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures the "Pipe Spec" shared parameter exists and is bound to
        /// OST_FabricationPipework as an instance parameter with UserModifiable = false.
        /// Safe to call multiple times — exits early if already bound.
        /// Must be called inside a Transaction.
        /// </summary>
        public static void EnsurePipeSpecParameter(Document doc, Application app)
        {
            if (IsPipeSpecBound(doc)) return;

            string sharedParamFile = GetSharedParamFilePath();
            string previousFile = app.SharedParametersFilename;
            try
            {
                EnsureSharedParamFile(sharedParamFile);
                app.SharedParametersFilename = sharedParamFile;

                DefinitionFile defFile = app.OpenSharedParameterFile();
                if (defFile == null)
                    throw new System.InvalidOperationException(
                        $"Could not open shared parameter file at: {sharedParamFile}");

                DefinitionGroup grp = defFile.Groups.get_Item(SharedParamGroup)
                                   ?? defFile.Groups.Create(SharedParamGroup);

                ExternalDefinition? def = grp.Definitions.get_Item(PipeSpecParamName)
                                            as ExternalDefinition;
                if (def == null)
                {
                    var opts = new ExternalDefinitionCreationOptions(
                        PipeSpecParamName, SpecTypeId.String.Text)
                    {
                        UserModifiable = false,
                        Visible        = true
                    };
                    def = grp.Definitions.Create(opts) as ExternalDefinition;
                }
                if (def == null)
                    throw new System.InvalidOperationException(
                        "Failed to create shared parameter definition.");

                var cats = doc.Settings.Categories;
                var cat  = cats.get_Item(BuiltInCategory.OST_FabricationPipework);
                var catSet = new CategorySet();
                catSet.Insert(cat);

                var binding = app.Create.NewInstanceBinding(catSet);
                doc.ParameterBindings.Insert(def, binding, GroupTypeId.Mechanical);
            }
            finally
            {
                app.SharedParametersFilename = previousFile;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Apply spec to a single part (no transaction — caller wraps if needed)
        // ─────────────────────────────────────────────────────────────────────────

        public static void ApplySpec(FabricationPart part, Dictionary<string, string> mappings)
        {
            string? svc = part.ServiceName;
            if (string.IsNullOrWhiteSpace(svc)) return;
            if (!mappings.TryGetValue(svc, out string? spec)) return;
            if (string.IsNullOrWhiteSpace(spec)) return;

            Parameter? p = ExportCommand.FindParameter(part, PipeSpecParamName);
            if (p != null && !p.IsReadOnly)
                p.Set(spec);
        }

        /// <summary>
        /// Applies mappings to ALL FabricationPart elements in the document.
        /// Wraps in its own Transaction.
        /// </summary>
        public static void ApplyToAll(Document doc, Dictionary<string, string> mappings)
        {
            using var tx = new Transaction(doc, "Apply Pipe Spec to All Parts");
            tx.Start();
            foreach (var part in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .WhereElementIsNotElementType()
                .Cast<FabricationPart>())
            {
                ApplySpec(part, mappings);
            }
            tx.Commit();
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────────

        private static bool IsPipeSpecBound(Document doc)
        {
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is Definition def &&
                    def.Name == PipeSpecParamName)
                    return true;
            }
            return false;
        }

        private static Schema GetOrCreateSchema()
        {
            Schema? existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldName, typeof(string));
            return builder.Finish();
        }

        private static string GetSharedParamFilePath()
        {
            string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir ?? string.Empty, "PcfExportSharedParams.txt");
        }

        private static void EnsureSharedParamFile(string path)
        {
            if (!File.Exists(path))
                File.WriteAllText(path, "# Revit Shared Parameter File\n# Do not edit manually\n");
        }

        private static IEnumerable<(string? Key, string? Value)> SplitJsonPairs(string body)
        {
            int i = 0;
            while (i < body.Length)
            {
                while (i < body.Length && (body[i] == ',' || body[i] == ' ')) i++;
                if (i >= body.Length) break;

                string? key = ReadJsonString(body, ref i);
                while (i < body.Length && body[i] != ':') i++;
                i++;
                string? val = ReadJsonString(body, ref i);
                yield return (key, val);
            }
        }

        private static string? ReadJsonString(string s, ref int i)
        {
            while (i < s.Length && s[i] != '"') i++;
            if (i >= s.Length) return null;
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i]); }
                else sb.Append(s[i]);
                i++;
            }
            i++;
            return sb.ToString();
        }

        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
