# Open Data Hub data-science runtime, tag runtime-datascience-ubi9-python-3.11-2024b-20250904
FROM --platform=linux/amd64 quay.io/opendatahub/workbench-images@sha256:427f7638a83a3efaf68f7dc95c9f323f2c093f33b9d15e46af8ceb26058fc2ea AS base

FROM base AS test
RUN pip install pytest==8.3.3
COPY src/run_analysis /opt/runner/src/run_analysis
COPY tests /opt/runner/tests
ENV PYTHONPATH=/opt/runner/src
WORKDIR /opt/runner
CMD ["python3", "-m", "pytest", "-q", "-p", "no:cacheprovider", "tests"]

FROM base AS runner
USER 0
COPY src/run_analysis /tmp/run_analysis
COPY --chmod=0755 bin/run-analysis /opt/app-root/bin/run-analysis
RUN cp -r /tmp/run_analysis "$(python3 -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')/run_analysis" \
    && rm -rf /tmp/run_analysis
USER 1001
ENTRYPOINT []
CMD ["run-analysis"]
