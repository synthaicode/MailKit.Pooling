# SMTP Session Management Sequence

This page captures Mermaid sequences for SMTP session management with multiple
SMTP servers, host failover, cooldown, and error handling behavior.

It is intended as a reusable design reference for pooled SMTP sending flows.

## Sequence Diagram

```mermaid
sequenceDiagram
    autonumber
    actor App as Application
    participant Sender as SMTP Sender
    participant Pool as Connection Pool
    participant Primary as "SMTP Host A (Primary)"
    participant Secondary as "SMTP Host B (Secondary)"
    participant Metrics as Metrics / Logs

    App->>Sender: Send mail request
    Sender->>Pool: Acquire lease
    Pool->>Primary: Reuse or create connection

    alt Primary is healthy
        Primary-->>Pool: Connection ready
        Pool-->>Sender: Lease returned
        Sender->>Primary: SMTP send
        Primary-->>Sender: Accepted
        Sender->>Pool: Return lease
        Pool->>Metrics: Record success, lease duration, pool state
        Sender-->>App: Send completed

    else Primary connect/auth failure
        Primary-->>Pool: Failure before send
        Pool->>Metrics: Record create failure
        Pool->>Metrics: Enter host cooldown
        Pool->>Secondary: Select next available host

        alt Secondary is healthy
            Secondary-->>Pool: Connection ready
            Pool-->>Sender: Lease returned from secondary
            Sender->>Secondary: SMTP send
            Secondary-->>Sender: Accepted
            Sender->>Pool: Return lease
            Pool->>Metrics: Record failover success
            Sender-->>App: Send completed via secondary
        else Secondary also unavailable
            Secondary-->>Pool: Failure before send
            Pool->>Metrics: Record create failure
            Pool->>Metrics: Record reconnect suppression / exhaustion
            Sender-->>App: Retryable or pool-exhausted failure
        end

    else Primary send failure before DATA
        Primary-->>Sender: Temporary / retryable failure
        Sender->>Pool: Invalidate lease
        Pool->>Metrics: Record classification and drop reason
        Pool->>Secondary: Attempt failover

        alt Secondary send succeeds
            Secondary-->>Sender: Accepted
            Sender->>Pool: Return secondary lease
            Pool->>Metrics: Record retry/failover success
            Sender-->>App: Send completed after retry
        else Secondary also fails
            Secondary-->>Sender: Failure
            Sender->>Pool: Invalidate lease
            Pool->>Metrics: Record repeated failure
            Sender-->>App: Retryable failure returned
        end

    else Primary failure after DATA
        Primary-->>Sender: Ambiguous outcome
        Sender->>Pool: Invalidate lease
        Pool->>Metrics: Record UnknownAfterData
        Pool->>Metrics: Record connection drop
        Sender-->>App: Ambiguous result, no blind retry

    else Pool saturated
        Pool->>Metrics: Record acquire wait time
        Pool->>Metrics: Record pool exhausted
        Sender-->>App: PoolExhausted failure
    end

    opt Host cooldown expires
        Pool->>Primary: Probe / allow new connection
        alt Primary recovers
            Primary-->>Pool: Connection ready
            Pool->>Metrics: Clear cooldown, mark host available
        else Primary still failing
            Primary-->>Pool: Failure
            Pool->>Metrics: Re-enter cooldown
        end
    end
```

## Per-Host Multi-Connection Flow

The following sequence focuses on the case where the pool may hold multiple
live connections for the same SMTP host.

It assumes:

- a minimum baseline may be created ahead of time during warmup
- additional capacity may still be created on demand
- host failover remains available when one host is blocked or unhealthy

```mermaid
sequenceDiagram
    autonumber
    actor App as Application
    participant Sender as SMTP Sender
    participant Pool as Connection Pool
    participant HostA as "SMTP Host A"
    participant HostB as "SMTP Host B"
    participant Metrics as Metrics / Logs

    rect rgb(245, 245, 245)
        Note over Pool,HostB: Startup / warmup phase
        Pool->>HostA: Create baseline idle connections A-1 ... A-n
        HostA-->>Pool: Baseline connections ready
        Pool->>HostB: Create baseline idle connections B-1 ... B-m
        HostB-->>Pool: Baseline connections ready
        Pool->>Metrics: Record initial pool state and created connections
    end

    App->>Sender: Send mail request
    Sender->>Pool: Acquire lease
    Pool->>Pool: Check idle connections for Host A

    alt Idle connection for Host A exists
        Pool-->>Sender: Lease idle connection A-1
        Sender->>HostA: SMTP send on A-1
        HostA-->>Sender: Accepted
        Sender->>Pool: Return lease A-1
        Pool->>Metrics: Record lease duration and idle reuse
        Sender-->>App: Send completed

    else No idle connection, but Host A can grow beyond baseline
        Pool->>HostA: Create new connection A-2
        HostA-->>Pool: Connection A-2 ready
        Pool-->>Sender: Lease new connection A-2
        Sender->>HostA: SMTP send on A-2
        HostA-->>Sender: Accepted
        Sender->>Pool: Return lease A-2
        Pool->>Metrics: Record on-demand connection creation and return
        Sender-->>App: Send completed

    else Host A at per-host capacity or temporarily blocked
        Pool->>Metrics: Record wait / host pressure
        alt Host B has idle or creatable capacity
            Pool->>HostB: Reuse or create connection B-1
            HostB-->>Pool: Connection B-1 ready
            Pool-->>Sender: Lease connection from Host B
            Sender->>HostB: SMTP send on B-1
            HostB-->>Sender: Accepted
            Sender->>Pool: Return lease B-1
            Pool->>Metrics: Record host failover under pressure
            Sender-->>App: Send completed via Host B
        else No host can provide a connection
            Pool->>Metrics: Record acquire wait time
            Pool->>Metrics: Record pool exhausted
            Sender-->>App: PoolExhausted failure
        end

    else Warm baseline exists, but chosen Host A connection becomes unhealthy during reuse
        Pool->>HostA: KeepAlive / connection validation on A-1
        HostA-->>Pool: Validation failure
        Pool->>Metrics: Record keepalive failure and drop A-1
        Pool->>Pool: Remove unhealthy connection
        alt Another warm or idle Host A connection is available
            Pool-->>Sender: Lease alternate connection A-2
            Sender->>HostA: SMTP send on A-2
            HostA-->>Sender: Accepted
            Sender->>Pool: Return lease A-2
            Sender-->>App: Send completed
        else Host A can create a replacement
            Pool->>HostA: Create replacement connection A-3
            HostA-->>Pool: Replacement connection ready
            Pool-->>Sender: Lease replacement connection A-3
            Sender->>HostA: SMTP send on A-3
            HostA-->>Sender: Accepted
            Sender->>Pool: Return lease A-3
            Pool->>Metrics: Record replacement after validation failure
            Sender-->>App: Send completed
        else Fail over to Host B
            Pool->>HostB: Reuse or create connection B-1
            HostB-->>Pool: Connection B-1 ready
            Pool-->>Sender: Lease connection from Host B
            Sender->>HostB: SMTP send on B-1
            HostB-->>Sender: Accepted
            Sender->>Pool: Return lease B-1
            Pool->>Metrics: Record unhealthy-reuse fallback
            Sender-->>App: Send completed via Host B
        end
    end
```
