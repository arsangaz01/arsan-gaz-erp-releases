using System;
using System.IO;
using System.Threading.Tasks;
using ArsanGazERP.Data;
namespace ArsanGazERP.Services;
public sealed class DatabaseBackupService
{
 private readonly GraphService _graph;
 public DatabaseBackupService(GraphService graph)=>_graph=graph;
 public async Task<string> BackupToOneDriveAsync()
 {
  await using ArsanGazDbContext db=new(); string source=db.DatabasePath;
  if(!File.Exists(source))throw new FileNotFoundException("Veritabanı bulunamadı.",source);
  string temp=Path.Combine(Path.GetTempPath(),$"arsangaz-erp-v4-{DateTime.Now:yyyyMMdd_HHmmss}.db");
  File.Copy(source,temp,true);
  try{return await _graph.UploadBackupAsync(temp);}finally{if(File.Exists(temp))File.Delete(temp);}
 }
}