param(
    [string]$Server = "JARVISNGUYEN",
    [string]$Database = "BillPayment"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Data

$connectionString = "Data Source=$Server;Initial Catalog=$Database;Integrated Security=True;Connect Timeout=5;Application Name=OpenHintSQLConnectionTest"
Write-Host "Testing SQL connection:"
Write-Host "  Server   : $Server"
Write-Host "  Database : $Database"
Write-Host "  Auth     : Windows"
Write-Host ""

$connection = New-Object System.Data.SqlClient.SqlConnection $connectionString

try {
    $connection.Open()

    $command = $connection.CreateCommand()
    $command.CommandText = @"
select @@SERVERNAME as ServerName, db_name() as DatabaseName, system_user as SystemUser;
select count(*) as ObjectCount
from sys.objects
where type in ('U', 'V');
"@

    $reader = $command.ExecuteReader()
    while ($reader.Read()) {
        Write-Host ("OK server={0} db={1} user={2}" -f $reader["ServerName"], $reader["DatabaseName"], $reader["SystemUser"])
    }

    if ($reader.NextResult()) {
        while ($reader.Read()) {
            Write-Host ("Tables/views visible: {0}" -f $reader["ObjectCount"])
        }
    }

    $reader.Close()
} catch {
    Write-Host ("FAIL: {0}: {1}" -f $_.Exception.GetType().FullName, $_.Exception.Message)
    exit 1
} finally {
    $connection.Dispose()
}
