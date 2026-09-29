using System.Collections.Generic;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Repositories;

public sealed class Repository<T> : IRepository<T> where T : class
{
    private readonly ArsanGazDbContext _database;
    public Repository(ArsanGazDbContext database) => _database = database;

    public Task<List<T>> GetAllAsync() => _database.Set<T>().AsNoTracking().ToListAsync();
    public async Task<T?> GetByIdAsync(int id) => await _database.Set<T>().FindAsync(id);

    public async Task<T> AddAsync(T entity)
    {
        _database.Set<T>().Add(entity);
        await _database.SaveChangesAsync();
        return entity;
    }

    public async Task UpdateAsync(T entity)
    {
        _database.Set<T>().Update(entity);
        await _database.SaveChangesAsync();
    }

    public async Task DeleteAsync(T entity)
    {
        _database.Set<T>().Remove(entity);
        await _database.SaveChangesAsync();
    }
}