"""Minimal, explicit QLoRA/SFT entry point for the canonical Huaxiazi dataset.

This script intentionally requires a HuggingFace-compatible base model directory;
an Ollama GGUF model is not silently treated as trainable.
"""
import argparse
import json
from pathlib import Path


def load_rows(path: Path):
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            row = json.loads(line)
            yield row["messages"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True, help="HuggingFace model directory or id")
    parser.add_argument("--train", default="datasets/v1/sft_train.jsonl")
    parser.add_argument("--output", default="training/artifacts/huaxiazi-lora")
    parser.add_argument("--epochs", type=float, default=2.0)
    parser.add_argument("--learning-rate", type=float, default=2e-5)
    parser.add_argument("--seed", type=int, default=20260907)
    parser.add_argument("--dry-run", action="store_true", help="validate inputs and dependencies without training")
    args = parser.parse_args()

    model_path = Path(args.model)
    if not model_path.exists() and "/" not in args.model:
        raise SystemExit("--model must be an existing HuggingFace directory or a valid Hub id; Ollama names are not accepted.")
    train_path = Path(args.train)
    rows = list(load_rows(train_path))
    if not rows:
        raise SystemExit("training file is empty")
    if args.dry_run:
        print(json.dumps({"dry_run": True, "model": args.model, "train_file": str(train_path), "rows": len(rows), "output": args.output, "cuda": _cuda_available()}, ensure_ascii=False))
        return 0

    try:
        import torch
        from datasets import Dataset
        from peft import LoraConfig, TaskType, get_peft_model
        from transformers import (AutoModelForCausalLM, AutoTokenizer, DataCollatorForLanguageModeling,
                                  Trainer, TrainingArguments)
    except ImportError as exc:
        raise SystemExit(f"SFT dependencies missing: {exc}. Install training/requirements-sft.txt") from exc

    tokenizer = AutoTokenizer.from_pretrained(args.model, use_fast=True)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    def encode(example):
        text = tokenizer.apply_chat_template(example["messages"], tokenize=False, add_generation_prompt=False)
        return tokenizer(text, truncation=True, max_length=2048)

    dataset = Dataset.from_dict({"messages": rows}).map(encode, remove_columns=["messages"])
    model = AutoModelForCausalLM.from_pretrained(args.model, torch_dtype=torch.float16 if torch.cuda.is_available() else None)
    model = get_peft_model(model, LoraConfig(r=16, lora_alpha=32, lora_dropout=0.05, task_type=TaskType.CAUSAL_LM, target_modules="all-linear"))
    model.print_trainable_parameters()
    training_args = TrainingArguments(output_dir=args.output, num_train_epochs=args.epochs, learning_rate=args.learning_rate,
                                      per_device_train_batch_size=2, gradient_accumulation_steps=16, warmup_ratio=0.05,
                                      logging_steps=10, save_strategy="epoch", seed=args.seed, report_to="none")
    trainer = Trainer(model=model, args=training_args, train_dataset=dataset,
                      data_collator=DataCollatorForLanguageModeling(tokenizer=tokenizer, mlm=False))
    trainer.train()
    trainer.save_model(args.output)
    tokenizer.save_pretrained(args.output)
    print(json.dumps({"output": args.output, "rows": len(rows), "seed": args.seed}, ensure_ascii=False))
    return 0


def _cuda_available():
    try:
        import torch
        return bool(torch.cuda.is_available())
    except ImportError:
        return False


if __name__ == "__main__":
    raise SystemExit(main())
