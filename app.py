import MySQLdb
import pandas as pd

info_server = {
    "host": "",
    "user": "",
    "password": "",
    "port": 3306,
    "db": "",
}

conn = MySQLdb.connect(**info_server)
cursor = conn.cursor()

# Table prefixes to check
prefixes = ["achats", "panier", "polling"]

# Excel writer to collect results, one sheet per table that has data
output_path = "tables_with_data.xlsx"
writer = pd.ExcelWriter(output_path, engine="openpyxl")

tables_with_data = []
tables_without_data = []
tables_with_errors = []

for prefix in prefixes:
    for week in range(15, 53):
        table_name = f"{prefix}_2026{week:02d}"
        try:
            # First check row count cheaply
            cursor.execute(f"SELECT COUNT(*) FROM {table_name}")
            count = cursor.fetchone()[0]

            if count > 0:
                print(f"Data found in: {table_name} ({count} rows)")
                tables_with_data.append((table_name, count))

                # Pull the actual data for this table
                df = pd.read_sql(f"SELECT * FROM {table_name}", conn)

                # Excel sheet names are limited to 31 characters
                sheet_name = table_name[:31]
                df.to_excel(writer, sheet_name=sheet_name, index=False)
            else:
                print(f"No data in: {table_name}")
                tables_without_data.append(table_name)

        except Exception as e:
            print(f"Error checking {table_name}: {e}")
            tables_with_errors.append((table_name, str(e)))

# Add a summary sheet listing which tables had data
summary_df = pd.DataFrame(tables_with_data, columns=["table_name", "row_count"])
summary_df.to_excel(writer, sheet_name="Summary", index=False)

writer.close()
cursor.close()
conn.close()

print("\n--- Summary ---")
print(f"Tables with data ({len(tables_with_data)}):")
for t, c in tables_with_data:
    print(f"  {t}: {c} rows")

print(f"\nTables with no data ({len(tables_without_data)})")
if tables_with_errors:
    print(f"\nTables with errors ({len(tables_with_errors)}):")
    for t, err in tables_with_errors:
        print(f"  {t}: {err}")

print(f"\nExcel file written to: {output_path}")
print("Done!")