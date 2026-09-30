using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
namespace ArsanGazERP.Services;
public sealed class DatabaseService
{
 public async Task InitializeAsync()
 {
  await using ArsanGazDbContext db=new();
  await db.Database.MigrateAsync();
  if(!await db.Products.AnyAsync())
  {
   db.Products.AddRange(New("OKSIJEN","Oksijen"),New("AZOT","Azot"),New("ARGON","Argon"),New("HELYUM","Helyum"),New("ASETILEN","Asetilen"),New("HIDROJEN","Hidrojen"),New("KURU-HAVA","Kuru Hava"),New("BALON-GAZI","Balon Gazı"));
   await db.SaveChangesAsync();
  }
 }
 private static Product New(string c,string n)=>new(){Code=c,Name=n,Unit="Adet",Currency="TRY",VatRate=20,IsActive=true};
}