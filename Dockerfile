# Build stage
FROM python:3.9-slim AS builder

# Set working directory
WORKDIR /build

# Install build dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    git \
    git-lfs \
    && rm -rf /var/lib/apt/lists/*

# Initialize git-lfs
RUN git lfs install

# Copy requirements
COPY requirements.txt .

# Install dependencies
RUN pip install --no-cache-dir -r requirements.txt

# Clone and prepare Kokoro files
RUN git clone https://huggingface.co/hexgrad/Kokoro-82M /tmp/kokoro && \
    mkdir -p /build/kokoro && \
    cp /tmp/kokoro/*.py /build/kokoro/ && \
    cp /tmp/kokoro/*.json /build/kokoro/ && \
    cp /tmp/kokoro/*.pth /build/kokoro/ && \
    cp -r /tmp/kokoro/voices /build/kokoro/ && \
    rm -rf /tmp/kokoro

# Final stage
FROM python:3.9-slim

# Create non-root user
RUN useradd -m -u 1000 appuser

# Install runtime dependencies only
RUN apt-get update && apt-get install -y --no-install-recommends \
    espeak-ng \
    && rm -rf /var/lib/apt/lists/*

# Set working directory
WORKDIR /app

# Copy Python dependencies from builder
COPY --from=builder /usr/local/lib/python3.9/site-packages/ /usr/local/lib/python3.9/site-packages/

# Copy Kokoro files from builder
COPY --from=builder /build/kokoro/ .

# Copy application code
COPY app.py .

# Change ownership of the application files
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose port
EXPOSE 8000

# Set environment variables
ENV PYTHONUNBUFFERED=1
ENV FLASK_APP=app.py
ENV FLASK_ENV=production

# Run the application
CMD ["python", "app.py"] 