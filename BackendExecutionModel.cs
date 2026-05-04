# Backend Execution Models — Study Guide

A personal study doc on execution models, workers, queues, and state — written from the perspective of someone coming from game development.

---

## 1. The Mental Shift From Game Dev

Game dev assumes:
- One process, one player (or a small known number).
- State lives in RAM.
- A deterministic loop runs at fixed FPS.
- The network is an opt-in feature.

Backend assumes:
- Many processes on many machines.
- Thousands or millions of users hitting the system concurrently.
- State lives in **databases**, because RAM dies when servers restart.
- There is **no main loop** — work happens reactively when requests arrive.
- The network is fundamentally unreliable.

Almost every "weird" backend concept exists to deal with one of those four facts.

---

## 2. State and Behavior Are Separated

In Unity, when 10,000 enemies are alive, you have 10,000 GameObject instances in memory, each ticking via `Update()`. State and behavior are colocated — the object *is* the running thing.

In backend, those two are deliberately separated:

- **State** = rows in a database. Cheap, durable, survives restarts.
- **Behavior** = stateless workers that read state, do work, write state back.

You can kill all the workers and restart them, and the system picks up exactly where it left off, because the truth lives in the database, not in any program's memory.

This is the single biggest mental shift coming from games. It's what makes backend systems both more annoying (more moving parts) and more robust (nothing is lost when things crash).

---

## 3. What Is an Execution Model?

An execution model is the architecture for *how* a system runs multi-step processes. Whenever you have step 1 → step 2 → step 3 (especially if any step can fail, take a long time, or run for many users), you have to decide:

- Where does the state live between steps?
- What happens if a step crashes halfway?
- How do steps communicate?
- How do you scale?

Three common answers:

### 3.1 Stateful Workflow Engine (Temporal-like, durable)

You write the workflow as if it's a normal function:

```
result1 = step1(input)
result2 = step2(result1)
result3 = step3(result2)
```

Behind the scenes, an engine (Temporal, Restate, AWS Step Functions) records the input and output of every step into a durable database. If a worker crashes after step 2, another worker picks up the same workflow, replays the history, and resumes at step 3 — automatically.

- **Key trait:** the engine owns the state. Code looks linear; retries, timeouts, and recovery are handled for you.
- **Good for:** long-running workflows (minutes to months), strong reliability, complex branching logic.
- **Trade-off:** heavy infrastructure, tied to the engine's SDK and programming model.

### 3.2 Event-Driven Step Processor (queue per step)

Each step is its own worker reading from a queue:

- Worker A pulls from `queue:step1`, does work, pushes to `queue:step2`.
- Worker B pulls from `queue:step2`, does work, pushes to `queue:step3`.

There is **no central orchestrator**. The flow is implicit in who reads/writes which queue. State lives in the messages and whatever database you use.

- **Key trait:** no engine, just queues and workers. Maximum decoupling.
- **Good for:** very high scale, steps with very different resource profiles, loose coupling between teams.
- **Trade-off:** no single place to ask "what's the status of user X?" Debugging and end-to-end reasoning are harder.

### 3.3 Compiled DAG Executed Per User

You define the workflow as a graph (nodes = steps, edges = dependencies). For each request, the system "compiles" the DAG into an execution plan and runs it — usually inside a single process, often in memory.

Examples: LangGraph, Inngest functions, Prefect flows, Airflow DAG runs.

- **Key trait:** state lives in the running execution. Durability is optional.
- **Good for:** short workflows (seconds to minutes), graph-shaped logic with branching and parallel steps.
- **Trade-off:** if the process dies, the run usually dies too.

> **DAG** stands for "Directed Acyclic Graph." It's just a math word for "a flowchart with arrows that don't loop back." A recipe is a DAG. A build pipeline is a DAG. The word doesn't imply anything about *how* it runs.

### 3.4 Side-by-Side

| | Stateful engine | Event-driven | Compiled DAG |
|---|---|---|---|
| Where state lives | Engine's database | In messages + your DB | In-process memory |
| Crash recovery | Automatic (replay) | Manual / queue redelivery | Usually lost |
| Mental model | "Function with magic" | "Pipes between services" | "Graph per request" |
| Best for | Long, reliable, complex | Massive scale, decoupled | Per-request, graph-shaped |
| Infra weight | Heavy | Medium | Light |
| Examples | Temporal, Restate, Step Functions | SQS+Lambda chains, Kafka pipelines | LangGraph, Inngest, Prefect |

The three trade off the same axes: **durability vs. simplicity vs. scalability**.

---

## 4. Core Vocabulary

### Workflow vs. Workflow Instance

- A **workflow** is the *template* — the diagram or definition. There's only one of those.
- A **workflow instance** is *one user's run* through that template.

If 10,000 users sign up today and each enters a 30-day journey, you have 10,000 instances — all running the same workflow, each at its own point in time.

Recipe vs. meal analogy:
- Recipe = the workflow definition (one document in a drawer).
- Pot on the stove = one instance (its own timer, ingredients, stage).
- 10,000 people cooking the same recipe = 10,000 instances.

**Critically:** in a properly designed backend system, an instance is **not a running program**. It's a **row in a database**:

```
journey_executions
┌──────┬─────────┬────────────┬──────────────┬──────────────────────┐
│  id  │ user_id │ journey_id │ current_step │   waiting_until      │
├──────┼─────────┼────────────┼──────────────┼──────────────────────┤
│ 9001 │  4471   │ welcome_v1 │ wait_7_days  │ 2026-05-11 14:00:00  │
│ 9002 │  4472   │ welcome_v1 │ send_push    │ NULL                 │
│ 9003 │  4473   │ welcome_v1 │ done         │ NULL                 │
└──────┴─────────┴────────────┴──────────────┴──────────────────────┘
```

Nothing is "running" for user 4471 right now. They're a row sitting in a database. The journey progresses when something pokes at that row.

### Workers

A **worker** is a small, always-running program that does one type of job. **It is a separate program — not a class inside your main app.**

On a server it looks like:

```
$ ps aux
api-server      running, PID 1250
email-worker    running, PID 1234
push-worker     running, PID 1235
push-worker     running, PID 1236  ← second copy for scale
push-worker     running, PID 1237  ← third copy
wait-scheduler  running, PID 1240
```

Each is a separate binary/process, often deployed as its own container.

Why separate programs and not classes?

1. **Independent scaling.** 100,000 pushes to send and no emails? Spin up 20 copies of `push-worker` and leave `email-worker` alone.
2. **Failure isolation.** `push-worker` crashes? API stays up. Other workers keep running.
3. **Independent deployment.** New version of `push-worker` ships without redeploying everything.

Inside each worker there's typically a class doing the actual logic (e.g., `class PushSender`). But the worker as a whole is a standalone program whose entire job is: loop forever, pull from queue, call that class, repeat.

### Queues

A **queue** is a list of pending jobs. Workers read from it; other workers (or schedulers, or APIs) write to it.

```
api-server                     queue                 email-worker
    │                            │                       │
    │  "send email to user 4471" │                       │
    ├───────────────────────────>│                       │
    │                            │                       │
    │                            │  picks up job         │
    │                            │<──────────────────────│
    │                            │                       │
    │                            │  ┌─ sends email
    │                            │  └─ done
```

Workers don't call each other's APIs. They communicate through a shared queue. The producer doesn't know which worker will handle the job — or if any worker is up at all. If none are running, the job waits.

Technically a queue is either:
- A dedicated piece of software like Redis, RabbitMQ, or Kafka.
- Or just a Postgres table with `SELECT ... FOR UPDATE SKIP LOCKED` (boring, works fine for moderate scale).

This is the **decoupling** that distributed systems care about — producer and consumer are independent.

---

## 5. Stateful vs. Event-Driven, Walked Through

Same scenario both ways: user signs up → welcome email → wait 7 days → push.

### Event-driven version

You write **no journey code as a single thing**. The journey emerges from how workers pass jobs around.

```
T=0:  user signs up
      → api-server inserts row in journey_executions
      → api-server pushes job onto email-queue

T=0:  email-worker pulls job
      → sends email
      → updates row: step='wait_7_days', waiting_until=T+7days
      → inserts row in scheduled_jobs

T=0 → T+7d: NOTHING happens for this user.
            The row sits in the database. No code runs. No memory used.

Every minute:
      wait-scheduler runs:
      "SELECT * FROM scheduled_jobs WHERE run_at <= NOW()"

T+7d: wait-scheduler finds the row
      → pushes job onto push-queue
      → marks scheduled_jobs row done

T+7d: push-worker pulls job
      → sends push
      → updates row: step='done'
```

There is **no single place** in the codebase that says "the journey is: email, then wait, then push." That sequence is implicit. It emerges from each worker writing the *next* job after finishing its own. The journey definition (JSON from the admin panel) acts as a recipe workers consult.

### Stateful engine version

You write **the entire journey as one function**:

```python
@workflow
def welcome_journey(user_id):
    send_email(user_id, "welcome")
    sleep(7 days)
    send_push(user_id, "come back!")
```

It looks like a normal function. **It is not.** What actually happens:

```
T=0:  api-server tells engine: "start welcome_journey for user 4471"
      → engine creates instance record
      → assigns to a worker
      → worker executes function
      → hits send_email() → engine logs "send_email, result=success"
      → hits sleep(7 days) → engine logs "sleeping until T+7d"
      → engine STOPS execution. Frees worker. Stores state.

T=0 → T+7d: nothing.

T+7d: engine wakes up the instance
      → assigns to a worker (possibly different from the original)
      → worker re-runs the function from the top
      → engine intercepts every call and REPLAYS logged results
        instead of re-doing them — until it hits a step that
        hasn't run yet (the send_push)
      → executes send_push for real, logs result
      → hits sleep, engine stops again
```

This trick is called **event sourcing + replay**. The function appears to run for 30 days. In reality it runs in short bursts and replays history from the start each time it resumes.

The replay is why workflow code has strict rules: no random numbers, no real timestamps, no direct I/O — anything non-deterministic has to go through the engine, or replay produces different results than the original.

### What changes between them

| | Event-driven | Stateful engine |
|---|---|---|
| Where the journey logic lives | Distributed across workers + a JSON definition | A single function |
| What progresses a user | Workers writing jobs to queues | Engine waking up the instance |
| What you build yourself | Tables, queues, scheduler, workers | Workflow functions + activity functions |
| What you don't build | Journey orchestration is implicit in worker wiring | Persistence, retries, scheduling, resume |
| Adding a new step type | New worker + new node type in JSON | New function call in workflow code |
| Visibility | Direct: query Postgres | Indirect: ask engine's API |
| Failure of a step | Handle in the worker | Engine retries automatically |
| Long sleeps | A `scheduled_jobs` table you scan | `sleep(30 days)` "just works" |
| Mental model | "Pipes between specialized workers" | "Normal function that lasts 30 days" |

### When the choice tilts which way

**Stateful engine wins when:**
- Workflows are written by engineers, in code.
- Branching, retry, and compensation logic is complex and annoying to express as data.
- You need sophisticated child workflows.
- Team has Temporal-style expertise already.

**Event-driven wins when:**
- Workflows are defined by non-engineers in a UI (so the workflow is *data*, not code).
- You want to query state directly with SQL.
- Team is more comfortable with "boring" infra (Postgres + Redis + workers).
- Adding step types should be a contained, well-scoped task.

A useful pattern: build event-driven *first*, but design your data model so you could swap in a stateful engine later. Clean `executions` + `scheduled_jobs` + step-handler patterns make that a refactor, not a rewrite.

---

## 6. Why "30-Day Workflows" Are the Hard Case

Any workflow that lives longer than a few minutes forces you to confront durability:

- A normal in-process function dies when the server restarts.
- Servers always restart. Deploys, crashes, autoscaling, OS updates.
- So long workflows have to externalize their state into a database.

That's why the compiled-DAG model is a non-starter for things like 30-day user journeys, while it's perfectly fine for "process this image through 5 filters" — duration determines whether durability is optional or mandatory.

This is also why event-sourcing-and-replay (Temporal) and queue-plus-database (event-driven) end up looking similar at the storage layer: both must persist progress somewhere durable. The difference is *who's in charge of reading and advancing it.*

---

## 7. The Bigger Picture (For Future Study)

Concepts that come up next, in rough order of usefulness:

- **Stateless services.** Why "no in-memory state" is the default — it's what lets you scale by adding more boxes.
- **Idempotency.** Making operations safe to retry. If you charge a card twice with the same request ID, only one charge happens.
- **At-least-once vs exactly-once delivery.** Queues redeliver messages. Workers must tolerate seeing the same job twice.
- **Backpressure.** What happens when producers outpace consumers. (Queues fill up, latency rises, eventually you drop work or slow producers down.)
- **Dead-letter queues.** Where jobs go when they fail repeatedly. You inspect these to debug.
- **Observability.** Logs, metrics, traces. With workers spread across machines, this is the only way to understand what's happening.
- **Sagas and compensation.** Multi-step operations across services where you can't use a database transaction. If step 4 fails, you have to "undo" steps 1-3 with compensating actions.
- **Outbox pattern.** Reliably publishing events when you also write to a database (so the two never disagree).

---

## 8. Recap in One Page

- Backend ≠ game dev. The biggest shift: **state goes in databases, not in process memory.**
- An **execution model** answers: where does state live, how do steps communicate, what survives crashes.
- Three families: **stateful engine** (function that magically lasts 30 days), **event-driven** (queues + workers cooperating), **compiled DAG** (in-memory graph, dies on crash).
- A **workflow instance** is a *row in a database*, not a running program.
- A **worker** is a *separate program*, not a class. Workers communicate through queues, never by calling each other's APIs.
- A **queue** decouples producer and consumer — they don't even need to be running at the same time.
- Long workflows force durability. Short workflows can skip it.
- For UI-defined journeys (admin panel produces JSON), event-driven fits naturally because the journey is data. For code-defined workflows with complex logic, stateful engines shine.
- Build with the data model in mind so you can migrate between models later without a rewrite.
