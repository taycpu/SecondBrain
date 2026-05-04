# LLM Sampling Parameters Control the "Creativity Dial" at Token Level

> **TL;DR**
> - Sampling parameters all operate on the **probability distribution over tokens** — they don't change the model's knowledge, just how it picks words.
> - **Temperature** scales the whole distribution; Top K/P/MinP narrow *which* tokens survive to be sampled.
> - **Min P** is smarter than Top P because it scales relative to model confidence, not a fixed threshold.
> - Repeat penalty discourages loops by penalizing already-used tokens in the logit stage.
> - These parameters are applied **in sequence** before a token is finally sampled.

---

## Key Learnings

### Temperature reshapes the whole probability curve — it doesn't pick tokens itself

**Temperature** divides logits before softmax, flattening (high temp) or sharpening (low temp) the distribution. At 1.0 nothing changes. At 0.1 the model almost always picks the single most likely token. At 1.5+ even unlikely tokens get a real chance. It's a prerequisite — it happens before any filtering.

### Top K is a hard count cap; Top P is a smarter cumulative cap

**Top K = 50** always keeps exactly 50 tokens. **Top P = 0.9** keeps however many tokens it takes to reach 90% cumulative probability — could be 3 tokens (confident model) or 200 (uncertain model). Top P adapts to context; Top K doesn't.

### Min P solves Top P's blind spot by anchoring to the top token

**Min P = 0.05** means: keep a token only if its probability is ≥ 5% of the *top token's* probability. When the model is very confident (top token = 80%), the bar is high. When uncertain (top token = 5%), almost everything survives. This makes Min P more robust at higher temperatures than Top P alone.

### Repeat penalty operates on raw logits before temperature — not after

**Repeat penalty** (e.g. 1.3) *divides* the logit score of tokens already seen in the context window. Applied early in the pipeline, so it interacts with temperature downstream. Value of 1.0 = no effect; higher = stronger discouragement of repetition.

### The full sampling pipeline has a fixed order

```
Raw logits
  → Repeat Penalty   (penalise already-used tokens)
  → Temperature      (scale the distribution)
  → Top K            (hard count filter)
  → Top P            (cumulative probability filter)
  → Min P            (relative probability floor)
  → Sample           (draw one token from survivors)
```

Understanding the order matters when combining params — e.g., high temperature before Top P means more tokens survive into the nucleus.

### "Greedy decoding" is just a sampling config, not a special mode

Temperature → 0 (or Top K = 1) collapses sampling into always picking the single most probable token. Good for deterministic/factual tasks; bad for anything creative.

---

## Gotchas

- **Top K and Top P fight each other if misconfigured.** If Top K = 5 but Top P = 0.99, Top K wins and you only ever sample from 5 tokens no matter what.
- **High temperature alone isn't enough for creativity** — if you don't also relax Top P/Min P, the nucleus is still small.
- **Repeat penalty too high causes incoherence** — the model avoids previously-used common words (like "the") even when they're grammatically required.
- **Min P is not yet in all UIs** — some older frontends only expose Temperature, Top K, Top P. Min P is a newer addition (popularised in llama.cpp / LM Studio).

---

## 🔁 Recall Prompts

*Cover the answers and try to answer from memory before peeking.*

<details>
<summary><strong>Q1: What does Temperature = 1.0 do to the probability distribution?</strong></summary>

Nothing — it leaves the raw softmax probabilities unchanged. Values below 1.0 sharpen (more deterministic), values above 1.0 flatten (more random).

</details>

<details>
<summary><strong>Q2: Why is Top P generally preferred over Top K?</strong></summary>

Top K picks a fixed number of tokens regardless of how probability is distributed. Top P (nucleus sampling) picks a dynamic number that covers a fixed fraction of the cumulative probability, so it adapts: few tokens when the model is confident, more when uncertain.

</details>

<details>
<summary><strong>Q3: How does Min P differ from Top P?</strong></summary>

Top P uses an absolute cumulative threshold. Min P uses a *relative* threshold anchored to the top token — a token survives only if its probability is at least X% of the top token's probability. This scales naturally with model confidence.

</details>

<details>
<summary><strong>Q4: At what stage in the pipeline does repeat penalty apply, and why does it matter?</strong></summary>

It applies to raw logits *before* temperature scaling. This matters because the penalty interacts with temperature — a high temperature will partially "undo" a mild repeat penalty, so you may need to increase penalty strength when using high temperatures.

</details>

<details>
<summary><strong>Q5: What interactive tools can you use to see tokenization visually?</strong></summary>

**Tiktokenizer** (tiktokenizer.vercel.app) for real-time color-coded tokens across multiple model vocabularies. **Hugging Face Tokenizer Playground** for broader model support. **Transformer Explainer** (poloclub.github.io) for seeing probability distributions at each generation step.

</details>
