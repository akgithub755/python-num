using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OnDemandServices.Controllers;

/// <summary>
/// One generic API for every table shown on the Table Management page.
/// Assumes PostgreSQL (current_schema(), LIMIT, udt_name casts).
/// Replace AppDbContext with your real DbContext class (Data folder).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TableManagementController : ControllerBase
{
    private const int MaxRows = 1000;

    // Whitelist: only these tables can ever be read or written.
    private static readonly string[] Tables =
    {
        "corresp_qr_libelle_client",
        "dicoview_questions",
        "dicoview_reponses",
        "input_codes_to_download",
        "purchaseattribute",
        "recapcontourmapping",
        "retailer_attributes",
        "retailer_attributes_services",
        "roles",
        "scenariospecpat",
        "schedulerprogram",
        "viewkey_toqr_map",
        "volegrulesdefault",
        "volegrulesfixedloose",
        "volegrulesformula"
    };

    private readonly AppDbContext _db;
    private readonly ILogger<TableManagementController> _logger;

    public TableManagementController(AppDbContext db, ILogger<TableManagementController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record ColumnMeta(string Name, string UdtName, bool IsKey);
    public record ColumnDto(string Name, string Type, bool IsKey);
    public record TableDataDto(List<ColumnDto> Columns, List<object?[]> Rows);

    public class SaveRequest
    {
        public List<Dictionary<string, string?>> Inserts { get; set; } = new();
        public List<Dictionary<string, string?>> Updates { get; set; } = new();
    }

    [HttpGet("tables")]
    public IActionResult GetTables() => Ok(Tables);

    [HttpGet("{table}")]
    public async Task<IActionResult> GetTable(string table, CancellationToken ct)
    {
        if (!TryResolve(table, out var name))
            return NotFound();

        try
        {
            var conn = await OpenAsync(ct);
            var cols = await LoadColumnsAsync(conn, name, ct);

            var rows = new List<object?[]>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {Q(name)} ORDER BY 1 LIMIT {MaxRows}";

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(values);
            }

            var dto = new TableDataDto(
                cols.Select(c => new ColumnDto(c.Name, c.UdtName, c.IsKey)).ToList(),
                rows);

            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load table {Table}", name);
            return StatusCode(500, new { Message = $"An error occurred while loading table '{name}'." });
        }
    }

    [HttpPost("{table}/save")]
    public async Task<IActionResult> Save(string table, [FromBody] SaveRequest request, CancellationToken ct)
    {
        if (!TryResolve(table, out var name))
            return NotFound();

        try
        {
            var conn = await OpenAsync(ct);
            var cols = await LoadColumnsAsync(conn, name, ct);

            if (request.Updates.Count > 0 && !cols.Any(c => c.IsKey))
                return BadRequest(new { Message = "This table has no primary key, rows cannot be updated." });

            await using var tx = await conn.BeginTransactionAsync(ct);

            foreach (var row in request.Inserts)
                await InsertAsync(conn, tx, name, cols, row, ct);

            foreach (var row in request.Updates)
                await UpdateAsync(conn, tx, name, cols, row, ct);

            await tx.CommitAsync(ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save table {Table}", name);
            return StatusCode(500, new { Message = $"Unable to save '{name}': {ex.Message}" });
        }
    }

    [HttpPost("{table}/delete")]
    public async Task<IActionResult> Delete(string table, [FromBody] Dictionary<string, string?> key, CancellationToken ct)
    {
        if (!TryResolve(table, out var name))
            return NotFound();

        try
        {
            var conn = await OpenAsync(ct);
            var cols = await LoadColumnsAsync(conn, name, ct);
            var keys = cols.Where(c => c.IsKey).ToList();

            if (keys.Count == 0)
                return BadRequest(new { Message = "This table has no primary key, rows cannot be deleted." });

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {Q(name)} WHERE {BuildKeyFilter(cmd, keys, key, 0)}";

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected == 0 ? NotFound() : NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete from table {Table}", name);
            return StatusCode(500, new { Message = $"Unable to delete from '{name}': {ex.Message}" });
        }
    }

    // ---------- SQL helpers ----------

    private static async Task InsertAsync(
        DbConnection conn, DbTransaction tx, string table,
        List<ColumnMeta> cols, Dictionary<string, string?> row, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(row, StringComparer.OrdinalIgnoreCase);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        var names = new List<string>();
        var placeholders = new List<string>();

        foreach (var c in cols)
        {
            // null / missing = let the database default (identity, default value, NULL) apply
            if (!values.TryGetValue(c.Name, out var v) || v is null)
                continue;

            var p = $"@p{names.Count}";
            names.Add(Q(c.Name));
            placeholders.Add($"CAST({p} AS {c.UdtName})");
            AddParam(cmd, p, v);
        }

        cmd.CommandText = names.Count == 0
            ? $"INSERT INTO {Q(table)} DEFAULT VALUES"
            : $"INSERT INTO {Q(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", placeholders)})";

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateAsync(
        DbConnection conn, DbTransaction tx, string table,
        List<ColumnMeta> cols, Dictionary<string, string?> row, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(row, StringComparer.OrdinalIgnoreCase);
        var keys = cols.Where(c => c.IsKey).ToList();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        var sets = new List<string>();
        foreach (var c in cols.Where(c => !c.IsKey))
        {
            if (!values.TryGetValue(c.Name, out var v))
                continue;

            var p = $"@p{sets.Count}";
            sets.Add($"{Q(c.Name)} = CAST({p} AS {c.UdtName})");
            AddParam(cmd, p, v);
        }

        if (sets.Count == 0)
            return;

        var where = BuildKeyFilter(cmd, keys, values, sets.Count);

        cmd.CommandText = $"UPDATE {Q(table)} SET {string.Join(", ", sets)} WHERE {where}";

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        if (affected == 0)
            throw new InvalidOperationException("A row was not found. Refresh the table and try again.");
    }

    private static string BuildKeyFilter(
        DbCommand cmd, List<ColumnMeta> keys, Dictionary<string, string?> source, int paramOffset)
    {
        var values = new Dictionary<string, string?>(source, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var k in keys)
        {
            if (!values.TryGetValue(k.Name, out var v) || v is null)
                throw new InvalidOperationException($"Primary key value '{k.Name}' is missing.");

            var p = $"@k{paramOffset + parts.Count}";
            parts.Add($"{Q(k.Name)} = CAST({p} AS {k.UdtName})");
            AddParam(cmd, p, v);
        }

        return string.Join(" AND ", parts);
    }

    private static void AddParam(DbCommand cmd, string name, string? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = DbType.String;
        p.Value = (object?)value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static async Task<List<ColumnMeta>> LoadColumnsAsync(DbConnection conn, string table, CancellationToken ct)
    {
        const string sql = @"
            SELECT c.column_name,
                   c.udt_name,
                   EXISTS (
                       SELECT 1
                       FROM information_schema.table_constraints tc
                       JOIN information_schema.key_column_usage k
                         ON k.constraint_name = tc.constraint_name
                        AND k.table_schema = tc.table_schema
                       WHERE tc.constraint_type = 'PRIMARY KEY'
                         AND tc.table_schema = c.table_schema
                         AND tc.table_name = c.table_name
                         AND k.column_name = c.column_name) AS is_key
            FROM information_schema.columns c
            WHERE c.table_schema = current_schema()
              AND c.table_name = @t
            ORDER BY c.ordinal_position";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@t", table);

        var result = new List<ColumnMeta>();

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var udt = reader.GetString(1);
            if (!Regex.IsMatch(udt, "^[A-Za-z0-9_]+$"))
                udt = "text";

            result.Add(new ColumnMeta(reader.GetString(0), udt, reader.GetBoolean(2)));
        }

        if (result.Count == 0)
            throw new InvalidOperationException($"Table '{table}' was not found in the database.");

        return result;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);
        return conn;
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static bool TryResolve(string table, out string name)
    {
        name = Tables.FirstOrDefault(t => t.Equals(table, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return name.Length > 0;
    }
}
