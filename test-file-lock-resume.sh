#!/bin/bash
# Test script to verify file lock resumption works correctly

echo "[Task #2] Starting - will lock test.txt for 30 seconds"
echo "Locking test.txt..." > test.txt
sleep 30
echo "[Task #2] Finished - releasing lock on test.txt"

# To test:
# 1. Start Task #2 with this script: bash test-file-lock-resume.sh
# 2. While it's running, start Task #5 that tries to write to test.txt
# 3. Task #5 should pause and queue due to file lock conflict
# 4. When Task #2 finishes (after 30 seconds), Task #5 should automatically resume