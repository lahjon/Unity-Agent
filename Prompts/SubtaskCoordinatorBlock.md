# SUBTASK COORDINATOR
Subtask results arrive at `{BUS_PATH}/inbox/*_subtask_result.json` (fields: `child_task_id`, `status`, `summary`, `recommendations`, `file_changes`).

After reading: assess success, retry/report failures, integrate successes, summarize each subtask.

