# Use Python 3.9 as base image
FROM python:3.9-slim

# Set environment variables to force CPU usage
ENV CUDA_VISIBLE_DEVICES=""
ENV FORCE_CPU=1
ENV TORCH_DEVICE="cpu"

# Set working directory
WORKDIR /app

# Install system dependencies
RUN apt-get update && apt-get install -y \
    build-essential \
    libsndfile1 \
    git \
    git-lfs \
    espeak-ng \
    && rm -rf /var/lib/apt/lists/*

# Initialize git-lfs
RUN git lfs install

# Clone the repository
RUN git clone https://huggingface.co/hexgrad/Kokoro-82M .

# Install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt \
    --extra-index-url https://download.pytorch.org/whl/cpu

# Copy application code
COPY . .

# Expose port (adjust as needed)
EXPOSE 8000

# Command to run the application
CMD ["gunicorn", "--worker-class", "eventlet", "-w", "1", "--bind", "0.0.0.0:8000", "--timeout", "300", "--keep-alive", "120", "app:app"] 