-- Disposable CI instance only. No query text, names, paths or application rows.
-- Each completed section survives a later DMV/permission error or interruption.
SET NOCOUNT ON;
SET LOCK_TIMEOUT 1000;
SET DEADLOCK_PRIORITY LOW;

BEGIN TRY
    SELECT 'CI_META_BEGIN';
    SELECT CONVERT(varchar(max), (SELECT
        CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(
            CONVERT(varchar(33), sqlserver_start_time, 126), ':', sqlserver_start_time_ms_ticks)), 2) AS engine_epoch,
        CONVERT(varchar(33), SYSUTCDATETIME(), 126) + 'Z' AS sql_timestamp_utc,
        ms_ticks - sqlserver_start_time_ms_ticks AS uptime_ms
        FROM sys.dm_os_sys_info FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
    SELECT 'CI_META_END';
END TRY BEGIN CATCH
    SELECT 'CI_META_UNAVAILABLE';
END CATCH;

BEGIN TRY
    SELECT 'CI_WAITS_BEGIN';
    SELECT CONVERT(varchar(max), COALESCE((SELECT wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms
        FROM sys.dm_os_wait_stats WHERE waiting_tasks_count > 0 OR wait_time_ms > 0
        FOR JSON PATH), N'[]'));
    SELECT 'CI_WAITS_END';
END TRY BEGIN CATCH
    SELECT 'CI_WAITS_UNAVAILABLE';
END CATCH;

BEGIN TRY
    SELECT 'CI_IO_BEGIN';
    SELECT CONVERT(varchar(max), COALESCE((SELECT v.database_id, v.file_id,
        -- The handle detects close/reopen; GUIDs/creation identity detect reused IDs.
        -- If a user database has no GUID, Python conservatively omits its deltas.
        CASE WHEN r.database_guid IS NOT NULL OR d.database_id <= 4 THEN
            CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(
                CONVERT(varchar(36), r.database_guid), ':', CONVERT(varchar(33), d.create_date, 126), ':',
                CONVERT(varchar(36), f.file_guid), ':', f.create_lsn, ':',
                CONVERT(varchar(128), v.file_handle, 2))), 2) END AS file_identity,
        CASE WHEN d.database_id = 2 THEN 'tempdb'
             WHEN d.database_id <= 4 THEN 'system'
             WHEN d.name LIKE N'Asap%Test%' OR d.name LIKE N'AsapPatronJourney[_]%'
                 THEN 'synthetic_test' ELSE 'other' END AS database_kind,
        CASE f.type WHEN 0 THEN 'data' WHEN 1 THEN 'log' ELSE 'other' END AS file_kind,
        v.sample_ms, v.num_of_reads, v.num_of_bytes_read, v.io_stall_read_ms,
        v.num_of_writes, v.num_of_bytes_written, v.io_stall_write_ms, v.io_stall
        FROM sys.dm_io_virtual_file_stats(NULL, NULL) v
        JOIN sys.master_files f ON f.database_id = v.database_id AND f.file_id = v.file_id
        JOIN sys.databases d ON d.database_id = v.database_id
        LEFT JOIN sys.database_recovery_status r ON r.database_id = v.database_id
        FOR JSON PATH), N'[]'));
    SELECT 'CI_IO_END';
END TRY BEGIN CATCH
    SELECT 'CI_IO_UNAVAILABLE';
END CATCH;

BEGIN TRY
    SELECT 'CI_REQUESTS_BEGIN';
    SELECT CONVERT(varchar(max), (SELECT COUNT_BIG(*) AS active_requests,
        COALESCE(SUM(CASE WHEN q.status IN ('running', 'runnable') THEN 1 ELSE 0 END), 0) AS running_or_runnable_requests,
        COALESCE(SUM(CASE WHEN q.status = 'suspended' THEN 1 ELSE 0 END), 0) AS waiting_requests,
        COUNT(DISTINCT CASE WHEN q.blocking_session_id > 0 THEN q.session_id END) AS blocked_sessions,
        MAX(CASE WHEN q.blocking_session_id > 0 THEN q.wait_time END) AS max_blocked_wait_ms
        FROM sys.dm_exec_requests q JOIN sys.dm_exec_sessions s ON s.session_id = q.session_id
        WHERE s.is_user_process = 1 AND q.session_id <> @@SPID
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
    SELECT 'CI_REQUESTS_END';
END TRY BEGIN CATCH
    SELECT 'CI_REQUESTS_UNAVAILABLE';
END CATCH;

BEGIN TRY
    SELECT 'CI_REQUEST_WAITS_BEGIN';
    SELECT CONVERT(varchar(max), COALESCE((SELECT q.wait_type, COUNT_BIG(*) AS request_count, MAX(q.wait_time) AS max_wait_ms
        FROM sys.dm_exec_requests q JOIN sys.dm_exec_sessions s ON s.session_id = q.session_id
        WHERE s.is_user_process = 1 AND q.session_id <> @@SPID AND q.wait_type IS NOT NULL
        GROUP BY q.wait_type FOR JSON PATH), N'[]'));
    SELECT 'CI_REQUEST_WAITS_END';
END TRY BEGIN CATCH
    SELECT 'CI_REQUEST_WAITS_UNAVAILABLE';
END CATCH;

BEGIN TRY
    SELECT 'CI_SCHEDULERS_BEGIN';
    SELECT CONVERT(varchar(max), (SELECT COUNT_BIG(*) AS online_schedulers,
        SUM(CONVERT(bigint, runnable_tasks_count)) AS runnable_tasks,
        MAX(runnable_tasks_count) AS max_scheduler_runnable_tasks,
        SUM(CONVERT(bigint, work_queue_count)) AS queued_work_items,
        SUM(CONVERT(bigint, active_workers_count)) AS active_workers,
        SUM(CONVERT(bigint, current_workers_count)) AS current_workers
        FROM sys.dm_os_schedulers WHERE status = 'VISIBLE ONLINE'
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
    SELECT 'CI_SCHEDULERS_END';
END TRY BEGIN CATCH
    SELECT 'CI_SCHEDULERS_UNAVAILABLE';
END CATCH;

BEGIN TRY
    SELECT 'CI_MEMORY_BEGIN';
    SELECT CONVERT(varchar(max), (SELECT physical_memory_in_use_kb, locked_page_allocations_kb,
        memory_utilization_percentage, page_fault_count,
        CONVERT(int, process_physical_memory_low) AS process_physical_memory_low,
        CONVERT(int, process_virtual_memory_low) AS process_virtual_memory_low
        FROM sys.dm_os_process_memory FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
    SELECT 'CI_MEMORY_END';
END TRY BEGIN CATCH
    SELECT 'CI_MEMORY_UNAVAILABLE';
END CATCH;
