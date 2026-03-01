using System;
using System.Collections.ObjectModel;
using System.Linq;
using HappyEngine;
using HappyEngine.Managers;

// Debug version of the test to understand the issue
class DebugTest
{
    static void Main()
    {
        var factory = new TaskFactory();

        // Create tasks like in the test
        var taskA = factory.CreateTask("test task", @"C:\Projects\Test", false, false, false, false, false, false);
        taskA.Status = AgentTaskStatus.Running;
        taskA.TaskNumber = 1001;

        var taskB = factory.CreateTask("test task", @"C:\Projects\Test", false, false, false, false, false, false);
        taskB.Status = AgentTaskStatus.Running;
        taskB.TaskNumber = 1002;

        Console.WriteLine($"TaskA ID: {taskA.Id}, TaskNumber: {taskA.TaskNumber}");
        Console.WriteLine($"TaskB ID: {taskB.Id}, TaskNumber: {taskB.TaskNumber}");

        var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

        // Test lookup
        var foundTask = activeTasks.FirstOrDefault(t => t.Id == taskA.Id);
        Console.WriteLine($"Found task by ID: {foundTask != null}");
        Console.WriteLine($"Found task TaskNumber: {foundTask?.TaskNumber}");
    }
}