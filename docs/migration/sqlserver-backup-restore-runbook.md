# SQL Server Backup/Restore Runbook (Cutover Prerequisite)

## Backup policy

- Full backup nightly (retain 14 days minimum).
- Differential backup every 6 hours.
- Transaction log backup every 15 minutes in production.

## Rehearsal checklist

1. Restore latest full + differential + logs to staging SQL Server.
2. Run data integrity checks (`DBCC CHECKDB`) on restored DB.
3. Execute API smoke tests against restored database.
4. Record Recovery Time Objective (RTO) and Recovery Point Objective (RPO).

## Cutover day

1. Freeze writes on PocketBase app.
2. Run final export/import delta.
3. Take pre-cutover full backup from SQL Server.
4. Switch traffic to ASP.NET service.
5. Monitor error rate, queue lag, and job outcomes for 60 minutes.

## Rollback trigger

- If any Severity-1 parity regression persists longer than 15 minutes, switch traffic back and restore from pre-cutover backup.
