using System;
using System.Diagnostics;

namespace Spritely
{
    /// <summary>
    /// Test for the configurable task timeout feature
    /// </summary>
    public static class TestTimeoutFeature
    {
        public static void VerifyTimeoutProperties()
        {
            Console.WriteLine("=== Testing Task Timeout Feature ===");

            // Test 1: AppConstants default value
            Debug.Assert(Constants.AppConstants.DefaultTaskTimeoutMinutes == 120,
                "DefaultTaskTimeoutMinutes should be 120");
            Debug.Assert(Constants.AppConstants.TaskTimeoutWarningPercent == 0.8,
                "TaskTimeoutWarningPercent should be 0.8");
            Console.WriteLine("✓ Constants defined correctly");

            // Test 2: AgentTaskData has TimeoutMinutes property
            var taskData = new AgentTaskData();
            Debug.Assert(taskData.TimeoutMinutes == null, "TimeoutMinutes should be nullable and default to null");
            taskData.TimeoutMinutes = 60;
            Debug.Assert(taskData.TimeoutMinutes == 60, "TimeoutMinutes should be settable");
            Console.WriteLine("✓ AgentTaskData.TimeoutMinutes property works");

            // Test 3: AgentTaskData has HasTimeoutWarning property
            Debug.Assert(taskData.HasTimeoutWarning == false, "HasTimeoutWarning should default to false");
            taskData.HasTimeoutWarning = true;
            Debug.Assert(taskData.HasTimeoutWarning == true, "HasTimeoutWarning should be settable");
            Console.WriteLine("✓ AgentTaskData.HasTimeoutWarning property works");

            // Test 4: AgentTask inherits properties
            var agentTask = new AgentTask();
            agentTask.TimeoutMinutes = 90;
            Debug.Assert(agentTask.TimeoutMinutes == 90, "AgentTask should have TimeoutMinutes");
            Console.WriteLine("✓ AgentTask inherits timeout properties");

            Console.WriteLine("\n=== All tests passed! ===");
        }
    }
}