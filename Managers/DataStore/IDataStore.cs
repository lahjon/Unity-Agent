using System.Collections.Generic;
using System.Threading.Tasks;

namespace Spritely.Managers.DataStore
{
    /// <summary>
    /// Generic data storage abstraction for persistent file-based I/O with versioning support.
    /// </summary>
    public interface IDataStore<T> where T : class
    {
        /// <summary>
        /// Loads data from the store. Returns default(T) if the store is empty or doesn't exist.
        /// Handles version migration transparently.
        /// </summary>
        Task<T?> LoadAsync();

        /// <summary>
        /// Persists data to the store with atomic write semantics.
        /// </summary>
        Task SaveAsync(T data);

        /// <summary>
        /// Deletes the backing store file if it exists.
        /// </summary>
        Task DeleteAsync();

        /// <summary>
        /// Returns true if the backing store file exists.
        /// </summary>
        bool Exists();
    }

    /// <summary>
    /// Data store for collections stored as JSONL (one JSON object per line).
    /// </summary>
    public interface IJsonlDataStore<T> where T : class
    {
        Task<List<T>> LoadAsync();
        Task SaveAsync(List<T> items);
        Task DeleteAsync();
        bool Exists();
        Task AppendAsync(T item);
        Task UpsertAsync(T item, System.Func<T, T, bool> matchPredicate);
    }

    /// <summary>
    /// Data store that partitions items into separate files by key (e.g. iteration_1.json, iteration_2.json).
    /// </summary>
    public interface IPartitionedDataStore<T> where T : class
    {
        Task<T?> LoadAsync(string key);
        Task<List<T>> LoadAllAsync();
        Task SaveAsync(string key, T data);
        Task DeleteAsync(string key);
        Task DeleteAllAsync();
        bool Exists(string key);
        List<string> ListKeys();
    }
}
