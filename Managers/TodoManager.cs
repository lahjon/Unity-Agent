using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HappyEngine.Models;

namespace HappyEngine.Managers
{
    public class TodoManager
    {
        private readonly string _todosFile;
        private readonly List<TodoItem> _todos = new();

        public List<TodoItem> Todos => _todos;

        public TodoManager(string appDataDir)
        {
            _todosFile = Path.Combine(appDataDir, "todos.json");
        }

        public async Task LoadTodosAsync()
        {
            try
            {
                if (!File.Exists(_todosFile)) return;
                var json = await File.ReadAllTextAsync(_todosFile).ConfigureAwait(false);
                var todos = JsonSerializer.Deserialize<List<TodoItem>>(json);
                if (todos != null)
                {
                    _todos.Clear();
                    _todos.AddRange(todos.Where(t => !t.IsCompleted).OrderBy(t => t.CreatedAt));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TodoManager", "Failed to load todos", ex);
            }
        }

        public void SaveTodos()
        {
            try
            {
                var json = JsonSerializer.Serialize(_todos, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_todosFile, json, "TodoManager");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TodoManager", "Failed to save todos", ex);
            }
        }

        public void AddTodo(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var todo = new TodoItem { Text = text.Trim() };
            _todos.Add(todo);
            SaveTodos();
        }

        public void CompleteTodo(TodoItem todo)
        {
            todo.IsCompleted = true;
            todo.CompletedAt = DateTime.UtcNow;
            SaveTodos();
        }

        public void RemoveTodo(TodoItem todo)
        {
            _todos.Remove(todo);
            SaveTodos();
        }
    }
}