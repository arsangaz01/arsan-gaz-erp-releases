using System;
using System.IO;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using Microsoft.Data.Sqlite;
namespace ArsanGazERP.Services;
public sealed class DatabaseBackupService
{
 private readonly GraphService _graph;
 public DatabaseBackupService(GraphService graph)=>_graph=graph;
 public async Task<string> BackupToOneDriveAsync()
 {
  if(ArsanDatabase.IsPostgres)
   return "Veriler sunucudaki PostgreSQL veritabanında ("+ArsanDatabase.Describe(DatabaseKind.Erp)+"). Yedekler sunucuda pg_dump ile alınır "+
    "(her gece 30 gün saklanır, saatlik yedek 2 gün). Bu modda OneDrive'a dosya yedeği atlandı.";
  await new CrmSchemaService().EnsureAsync();
  await using ArsanGazDbContext db=new();
  string[] sources=[db.DatabasePath,CrmDbContext.SharedDatabasePath];
  string timestamp=DateTime.Now.ToString("yyyyMMdd_HHmmss");
  string[] snapshots=
  [
   Path.Combine(Path.GetTempPath(),$"arsangaz-erp-v4-{timestamp}-{Guid.NewGuid():N}.db"),
   Path.Combine(Path.GetTempPath(),$"arsangaz-crm-{timestamp}-{Guid.NewGuid():N}.db")
  ];
  try
  {
   for(int index=0;index<sources.Length;index++)
   {
    if(!File.Exists(sources[index]))throw new FileNotFoundException("Yedeklenecek veritabanı bulunamadı.",sources[index]);
    await CreateSnapshotAsync(sources[index],snapshots[index]);
   }
   string erpBackup=await _graph.UploadBackupAsync(snapshots[0]);
   string crmBackup=await _graph.UploadBackupAsync(snapshots[1]);
   return erpBackup+Environment.NewLine+crmBackup;
  }
  finally
  {
   foreach(string snapshot in snapshots)if(File.Exists(snapshot))File.Delete(snapshot);
  }
 }

 private static async Task CreateSnapshotAsync(string sourcePath,string destinationPath)
 {
  SqliteConnectionStringBuilder sourceSettings=new(){DataSource=sourcePath,Mode=SqliteOpenMode.ReadOnly,DefaultTimeout=30};
  SqliteConnectionStringBuilder destinationSettings=new(){DataSource=destinationPath,Mode=SqliteOpenMode.ReadWriteCreate,DefaultTimeout=30};
  await using SqliteConnection source=new(sourceSettings.ToString());
  await using SqliteConnection destination=new(destinationSettings.ToString());
  await source.OpenAsync();
  await destination.OpenAsync();
  source.BackupDatabase(destination);
 }
}