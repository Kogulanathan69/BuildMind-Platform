from fastapi import FastAPI

app = FastAPI(title="BuildMind Python Services", version="0.1.0")

@app.get("/health")
def health() -> dict[str, str]:
    return {"service": "buildmind-python", "status": "ok"}
