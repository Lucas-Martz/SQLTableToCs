// Program.cs - .NET 8 - Genera clase POO con campos privados, constructores y data annotations.
// Ajustá tu cadena de conexión:
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

internal static class Program
{
    // EDITAR: cadena de conexión con autenticación de Windows
    private const string CONNECTION_STRING = "Database";

    // Ruta fija de salida (como pediste)
    private const string OUTPUT_DIR = @"DestinationFolder";

    private static int Main()
    {
        Console.Write("Esquema (ENTER para 'dbo'): ");
        var schemaInput = Console.ReadLine();
        string schema = string.IsNullOrWhiteSpace(schemaInput) ? "dbo" : schemaInput.Trim();

        Console.Write("Nombre de la tabla: ");
        string table = (Console.ReadLine() ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(table))
        {
            Console.WriteLine("No ingresaste el nombre de la tabla. Cancelado.");
            return 1;
        }

        try
        {
            using var cn = new SqlConnection(CONNECTION_STRING);
            cn.Open();

            var columns = LoadColumns(cn, schema, table); // columnas con tipo, nulabilidad y longitud
            if (columns.Count == 0)
            {
                Console.WriteLine($"No se encontraron columnas para [{schema}].[{table}].");
                return 2;
            }

            var pkCols = LoadPrimaryKeys(cn, schema, table);
            var identityCols = LoadIdentityColumns(cn, schema, table);
            var fkInfo = LoadForeignKeys(cn, schema, table); // col -> (refSchema, refTable, refCol)

            string className = ToPascal(table);
            string code = GenerateClassCode(className, schema, table, columns, pkCols, identityCols, fkInfo);

            Directory.CreateDirectory(OUTPUT_DIR);
            string filePath = Path.Combine(OUTPUT_DIR, $"Cls{className}.cs");
            File.WriteAllText(filePath, code, new UTF8Encoding(false));

            Console.WriteLine($"OK. Generado: {filePath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            return 99;
        }
    }

    // ===== Modelos =====
    private sealed class Col
    {
        public string Name { get; }
        public string SqlType { get; }
        public bool IsNullable { get; }
        public int? MaxLen { get; }
        public int Ordinal { get; }

        public Col(string name, string sqlType, bool isNullable, int? maxLen, int ordinal)
        {
            Name = name;
            SqlType = sqlType;
            IsNullable = isNullable;
            MaxLen = maxLen;
            Ordinal = ordinal;
        }
    }

    // ===== Lectura de esquema =====
    private static List<Col> LoadColumns(SqlConnection cn, string schema, string table)
    {
        const string sql = @"
SELECT 
    c.COLUMN_NAME,
    c.DATA_TYPE,
    CASE c.IS_NULLABLE WHEN 'YES' THEN 1 ELSE 0 END AS IS_NULLABLE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.ORDINAL_POSITION
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
ORDER BY c.ORDINAL_POSITION;";

        using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var list = new List<Col>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string name = rd.GetString(0);
            string type = rd.GetString(1);
            bool nullable = rd.GetInt32(2) == 1;
            int? maxLen = rd.IsDBNull(3) ? (int?)null : Convert.ToInt32(rd.GetValue(3), CultureInfo.InvariantCulture);
            int ordinal = Convert.ToInt32(rd.GetValue(4), CultureInfo.InvariantCulture);
            list.Add(new Col(name, type, nullable, maxLen, ordinal));
        }
        return list;
    }

    private static HashSet<string> LoadPrimaryKeys(SqlConnection cn, string schema, string table)
    {
        const string sql = @"
SELECT kcu.COLUMN_NAME
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
  ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
 WHERE tc.TABLE_SCHEMA = @schema AND tc.TABLE_NAME = @table
   AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY';";

        using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) set.Add(rd.GetString(0));
        return set;
    }

    private static HashSet<string> LoadIdentityColumns(SqlConnection cn, string schema, string table)
    {
        const string sql = @"
SELECT c.name
FROM sys.columns c
JOIN sys.tables t   ON c.object_id = t.object_id
JOIN sys.schemas s  ON t.schema_id = s.schema_id
WHERE s.name = @schema AND t.name = @table AND c.is_identity = 1;";

        using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) set.Add(rd.GetString(0));
        return set;
    }

    private sealed class FkRef
    {
        public string RefSchema { get; }
        public string RefTable { get; }
        public string RefColumn { get; }
        public FkRef(string s, string t, string c) { RefSchema = s; RefTable = t; RefColumn = c; }
    }

    private static Dictionary<string, FkRef> LoadForeignKeys(SqlConnection cn, string schema, string table)
    {
        const string sql = @"
SELECT 
    pc.name  AS ParentColumn,
    s2.name  AS RefSchema,
    t2.name  AS RefTable,
    rc.name  AS RefColumn
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables t1 ON t1.object_id = fk.parent_object_id
JOIN sys.schemas s1 ON s1.schema_id = t1.schema_id
JOIN sys.columns pc ON pc.object_id = t1.object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.tables t2 ON t2.object_id = fk.referenced_object_id
JOIN sys.schemas s2 ON s2.schema_id = t2.schema_id
JOIN sys.columns rc ON rc.object_id = t2.object_id AND rc.column_id = fkc.referenced_column_id
WHERE s1.name = @schema AND t1.name = @table;";

        using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var map = new Dictionary<string, FkRef>(StringComparer.OrdinalIgnoreCase);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string parentCol = rd.GetString(0);
            string rsch = rd.GetString(1);
            string rtab = rd.GetString(2);
            string rcol = rd.GetString(3);
            map[parentCol] = new FkRef(rsch, rtab, rcol);
        }
        return map;
    }

    // ===== Generación =====
    private static string GenerateClassCode(
        string className,
        string schema,
        string table,
        List<Col> cols,
        HashSet<string> pkCols,
        HashSet<string> identityCols,
        Dictionary<string, FkRef> fkInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Archivo generado automáticamente.");
        sb.AppendLine("// Origen: [" + schema + "].[" + table + "]");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
        sb.AppendLine();
        sb.AppendLine("[Table(\"" + table + "\", Schema = \"" + schema + "\")]");
        sb.AppendLine("public class " + SanitizeIdentifier("Cls" + className));
        sb.AppendLine("{");

        // ----- Campos privados -----
        foreach (var c in cols)
        {
            string csType = MapSqlToCSharp(c.SqlType, c.IsNullable);
            string fieldName = "var" + c.Name;
            sb.AppendLine("    private " + csType + " " + fieldName + ";");
        }
        sb.AppendLine();

        // ----- Constructor por defecto -----
        sb.AppendLine("    public " + SanitizeIdentifier("Cls" + className) + "() { }");
        sb.AppendLine();

        // ----- Constructor completo (excluye IDENTITY) -----
        var ctorParams = new List<string>();
        foreach (var c in cols)
        {
            if (identityCols.Contains(c.Name)) continue; // no pedir identity en el ctor
            string csType = MapSqlToCSharp(c.SqlType, c.IsNullable);
            ctorParams.Add(csType + " " + ToCamel(c.Name));
        }
        if (ctorParams.Count > 0)
        {
            sb.AppendLine("    public " + SanitizeIdentifier("Cls" + className) + "(" + string.Join(", ", ctorParams) + ")");
            sb.AppendLine("    {");
            foreach (var c in cols)
            {
                if (identityCols.Contains(c.Name)) continue;
                string camel = c.Name;
                string fieldName = "var" + camel;
                sb.AppendLine("        this." + fieldName + " = " + camel + ";");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ----- Propiedades públicas -----
        foreach (var c in cols)
        {
            string propName = ToPascal(c.Name);
            string csType = MapSqlToCSharp(c.SqlType, c.IsNullable);
            string field = "_" + ToCamel(c.Name);

            // Atributos
            // Column
            sb.AppendLine("    //[Column(\"" + c.Name + "\")]");

            // MaxLength si aplica
            if (c.MaxLen.HasValue && c.MaxLen.Value > 0 && IsTextType(c.SqlType))
            {
                sb.AppendLine("    //[MaxLength(" + c.MaxLen.Value.ToString(CultureInfo.InvariantCulture) + ")]");
            }

            // Key / Identity
            if (pkCols.Contains(c.Name))
                sb.AppendLine("    //[Key]");
            if (identityCols.Contains(c.Name))
                sb.AppendLine("    //[DatabaseGenerated(DatabaseGeneratedOption.Identity)]");

            // Comentario para FK (no creamos navegación para no romper dependencias)
            if (fkInfo.TryGetValue(c.Name, out var fk))
            {
                sb.AppendLine("    // FK -> [" + fk.RefSchema + "].[" + fk.RefTable + "].[" + fk.RefColumn + "]");
                // Si querés navegación, descomentá la siguiente línea y creá la clase referida:
                // sb.AppendLine($"    public virtual Cls{ToPascal(fk.RefTable)} {ToPascal(fk.RefTable)} {{ get; set; }}");
            }

            // Propiedad con backing field
            sb.AppendLine("    public " + csType + " " + propName);
            sb.AppendLine("    {");
            sb.AppendLine("        get => " + field + ";");
            sb.AppendLine("        set => " + field + " = value;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    // ===== Utilidades =====
    private static bool IsTextType(string sqlType)
    {
        string t = (sqlType ?? "").ToLowerInvariant();
        return t == "varchar" || t == "nvarchar" || t == "char" || t == "nchar" || t == "text" || t == "ntext";
    }

    private static string MapSqlToCSharp(string sqlType, bool nullable)
    {
        string core = (sqlType ?? "").ToLowerInvariant() switch
        {
            "tinyint" => "byte",
            "smallint" => "short",
            "int" => "int",
            "bigint" => "long",
            "bit" => "bool",
            "decimal" => "decimal",
            "numeric" => "decimal",
            "money" => "decimal",
            "smallmoney" => "decimal",
            "float" => "double",
            "real" => "float",
            "date" => "DateTime",
            "datetime" => "DateTime",
            "datetime2" => "DateTime",
            "smalldatetime" => "DateTime",
            "time" => "TimeSpan",
            "datetimeoffset" => "DateTimeOffset",
            "char" => "string",
            "nchar" => "string",
            "varchar" => "string",
            "nvarchar" => "string",
            "text" => "string",
            "ntext" => "string",
            "uniqueidentifier" => "Guid",
            "binary" => "byte[]",
            "varbinary" => "byte[]",
            "image" => "byte[]",
            "xml" => "string",
            _ => "string"
        };

        if (core == "string" || core.EndsWith("[]")) return core;
        return nullable ? core + "?" : core;
    }

    private static string SanitizeIdentifier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Entidad";
        var s = new string(raw.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (string.IsNullOrEmpty(s)) s = "Entidad";
        if (char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    private static string ToPascal(string raw)
    {
        raw = (raw ?? "").Replace("_", " ").Replace("-", " ");
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            var lower = p.ToLowerInvariant();
            sb.Append(char.ToUpperInvariant(lower[0]));
            if (lower.Length > 1) sb.Append(lower[1..]);
        }
        return SanitizeIdentifier(sb.ToString());
    }

    private static string ToCamel(string raw)
    {
        var pascal = ToPascal(raw);
        if (string.IsNullOrEmpty(pascal)) return "campo";
        return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
    }
}