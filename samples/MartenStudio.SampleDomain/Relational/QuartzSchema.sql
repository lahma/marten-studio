-- Quartz.NET's own PostgreSQL job store schema, vendored into a `quartz` schema for the Marten Studio
-- sample, so that the database browser has a real third-party schema to show next to the Marten store.
--
-- Source:  Quartz.NET, https://github.com/quartznet/quartznet
--          database/tables/tables_postgres.sql as released in v4.3.0 (the fourteen-table schema; the 4.4
--          line adds qrtz_job_status and one more history index, and is not what this file carries)
-- Licence: Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0),
--          as Quartz.NET distributes it (license.txt in that repository). Distributed on an "AS IS"
--          BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND.
--
-- Changes from the original, and nothing else:
--   * every table and index is qualified with the `quartz` schema, which this script creates;
--   * the leading DROP block is gone - this runs on every start of the sample and must never drop
--     anything;
--   * CREATE TABLE / CREATE INDEX are IF NOT EXISTS, so running it again is a no-op;
--   * the schema carries a COMMENT saying where it came from.
-- Column names, types, keys, foreign keys, ON DELETE actions and index definitions are Quartz.NET's own,
-- byte for byte where the qualification allows. Keep it that way: the point of this file is that it is
-- the real thing.

CREATE SCHEMA IF NOT EXISTS quartz;

COMMENT ON SCHEMA quartz IS 'Quartz.NET job store (database/tables/tables_postgres.sql, Apache-2.0), vendored by the Marten Studio sample. Not a Marten schema.';

CREATE TABLE IF NOT EXISTS quartz.qrtz_job_details
  (
    sched_name TEXT NOT NULL,
    job_name TEXT NOT NULL,
    job_group TEXT NOT NULL,
    description TEXT NULL,
    job_class_name TEXT NOT NULL,
    is_durable BOOL NOT NULL,
    is_nonconcurrent BOOL NOT NULL,
    is_update_data BOOL NOT NULL,
    requests_recovery BOOL NOT NULL,
    job_data BYTEA NULL,
    PRIMARY KEY (sched_name, job_name, job_group)
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_triggers
  (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    job_name TEXT NOT NULL,
    job_group TEXT NOT NULL,
    description TEXT NULL,
    next_fire_time BIGINT NULL,
    prev_fire_time BIGINT NULL,
    priority INTEGER NULL,
    trigger_state TEXT NOT NULL,
    trigger_type TEXT NOT NULL,
    start_time BIGINT NOT NULL,
    end_time BIGINT NULL,
    calendar_name TEXT NULL,
    misfire_instr SMALLINT NULL,
    misfire_orig_fire_time BIGINT NULL,
    execution_group VARCHAR(200) NULL,
    preferred_node VARCHAR(200) NULL,
    preferred_node_auto BOOL NOT NULL DEFAULT FALSE,
    retry_policy VARCHAR(250) NULL,
    retry_attempt INTEGER NULL,
    continues_trigger_name TEXT NULL,
    continues_trigger_group TEXT NULL,
    continuation_condition INTEGER NULL,
    overlap_policy INTEGER NULL,
    pause_reason VARCHAR(250) NULL,
    paused_by VARCHAR(200) NULL,
    paused_at BIGINT NULL,
    job_data BYTEA NULL,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, job_name, job_group)
      REFERENCES quartz.qrtz_job_details (sched_name, job_name, job_group)
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_simple_triggers
  (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    repeat_count BIGINT NOT NULL,
    repeat_interval BIGINT NOT NULL,
    times_triggered BIGINT NOT NULL,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, trigger_name, trigger_group)
      REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group)
      ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_simprop_triggers
  (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    str_prop_1 TEXT NULL,
    str_prop_2 TEXT NULL,
    str_prop_3 TEXT NULL,
    int_prop_1 INTEGER NULL,
    int_prop_2 INTEGER NULL,
    long_prop_1 BIGINT NULL,
    long_prop_2 BIGINT NULL,
    dec_prop_1 NUMERIC NULL,
    dec_prop_2 NUMERIC NULL,
    bool_prop_1 BOOL NULL,
    bool_prop_2 BOOL NULL,
    time_zone_id TEXT NULL,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, trigger_name, trigger_group)
      REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group)
      ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_cron_triggers
  (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    cron_expression TEXT NOT NULL,
    time_zone_id TEXT,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, trigger_name, trigger_group)
      REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group)
      ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_blob_triggers
  (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    blob_data BYTEA NULL,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, trigger_name, trigger_group)
      REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group)
      ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_calendars
  (
    sched_name TEXT NOT NULL,
    calendar_name TEXT NOT NULL,
    calendar BYTEA NOT NULL,
    PRIMARY KEY (sched_name, calendar_name)
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_paused_trigger_grps
  (
    sched_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    pause_reason VARCHAR(250) NULL,
    paused_by VARCHAR(200) NULL,
    paused_at BIGINT NULL,
    PRIMARY KEY (sched_name, trigger_group)
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_paused_job_grps
  (
    sched_name TEXT NOT NULL,
    job_group TEXT NOT NULL,
    pause_reason VARCHAR(250) NULL,
    paused_by VARCHAR(200) NULL,
    paused_at BIGINT NULL,
    PRIMARY KEY (sched_name, job_group)
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_fired_triggers
  (
    sched_name TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    instance_name TEXT NOT NULL,
    fired_time BIGINT NOT NULL,
    sched_time BIGINT NOT NULL,
    priority INTEGER NOT NULL,
    state TEXT NOT NULL,
    job_name TEXT NULL,
    job_group TEXT NULL,
    is_nonconcurrent BOOL NOT NULL,
    requests_recovery BOOL NULL,
    execution_group VARCHAR(200) NULL,
    progress INTEGER NULL,
    progress_message VARCHAR(250) NULL,
    PRIMARY KEY (sched_name, entry_id)
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_scheduler_state
  (
    sched_name TEXT NOT NULL,
    instance_name TEXT NOT NULL,
    last_checkin_time BIGINT NOT NULL,
    checkin_interval BIGINT NOT NULL,
    PRIMARY KEY (sched_name, instance_name)
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_locks
  (
    sched_name TEXT NOT NULL,
    lock_name TEXT NOT NULL,
    PRIMARY KEY (sched_name, lock_name)
);

-- The two execution history tables. Optional: only a store configured with
-- UsePersistentStore(s => s.UseExecutionHistory()) reads or writes them, and nothing else in
-- this schema references them.
CREATE TABLE IF NOT EXISTS quartz.qrtz_execution_history
  (
    sched_name TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    instance_name TEXT NOT NULL,
    job_name TEXT NOT NULL,
    job_group TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    fired_time BIGINT NOT NULL,
    run_time BIGINT NOT NULL,
    succeeded BOOL NOT NULL,
    error_message TEXT NULL,
    retry_attempt INTEGER NOT NULL DEFAULT 0,
    retry_scheduled BOOL NOT NULL DEFAULT FALSE,
    execution_log TEXT NULL,
    PRIMARY KEY (sched_name, entry_id)
);

CREATE TABLE IF NOT EXISTS quartz.qrtz_misfire_history
  (
    sched_name TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    instance_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    job_name TEXT NULL,
    job_group TEXT NULL,
    misfire_time BIGINT NOT NULL,
    sched_time BIGINT NULL,
    reason INTEGER NULL,
    PRIMARY KEY (sched_name, entry_id)
);

CREATE INDEX IF NOT EXISTS idx_qrtz_j_g_n ON quartz.qrtz_job_details (sched_name, job_group, job_name);
CREATE INDEX IF NOT EXISTS idx_qrtz_t_j ON quartz.qrtz_triggers (sched_name, job_name, job_group);
CREATE INDEX IF NOT EXISTS idx_qrtz_t_c ON quartz.qrtz_triggers (sched_name, calendar_name);
CREATE INDEX IF NOT EXISTS idx_qrtz_t_g_n ON quartz.qrtz_triggers (sched_name, trigger_group, trigger_name);
CREATE INDEX IF NOT EXISTS idx_qrtz_t_nft_st ON quartz.qrtz_triggers (sched_name, trigger_state, next_fire_time asc, priority desc, misfire_instr);
CREATE INDEX IF NOT EXISTS idx_qrtz_ft_inst_job_req_rcvry ON quartz.qrtz_fired_triggers (sched_name, instance_name, requests_recovery);
CREATE INDEX IF NOT EXISTS idx_qrtz_ft_j_g ON quartz.qrtz_fired_triggers (sched_name, job_name, job_group);
CREATE INDEX IF NOT EXISTS idx_qrtz_ft_t_g ON quartz.qrtz_fired_triggers (sched_name, trigger_name, trigger_group);
CREATE INDEX IF NOT EXISTS idx_qrtz_eh_fired_time ON quartz.qrtz_execution_history (sched_name, fired_time);
CREATE INDEX IF NOT EXISTS idx_qrtz_eh_inst ON quartz.qrtz_execution_history (sched_name, instance_name);
CREATE INDEX IF NOT EXISTS idx_qrtz_mh_misfire_time ON quartz.qrtz_misfire_history (sched_name, misfire_time);
CREATE INDEX IF NOT EXISTS idx_qrtz_mh_inst ON quartz.qrtz_misfire_history (sched_name, instance_name);
