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
LABEL org.opencontainers.image.title="5s-tes/python-runner" \
      org.opencontainers.image.base.name="quay.io/opendatahub/workbench-images:runtime-datascience-ubi9-python-3.11-2024b-20250904" \
      org.opencontainers.image.base.digest="sha256:427f7638a83a3efaf68f7dc95c9f323f2c093f33b9d15e46af8ceb26058fc2ea"
USER 0
COPY src/run_analysis /tmp/run_analysis
COPY --chmod=0755 bin/run-analysis /opt/app-root/bin/run-analysis
COPY list-libraries.py /tmp/list-libraries.py
RUN cp -r /tmp/run_analysis "$(python3 -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')/run_analysis" \
    && rm -rf /tmp/run_analysis
# Researcher code must not install packages. Remove every installer: the venv's
# pip and micropipenv, the system Python 3.11's pip, and the pip wheels that
# ensurepip installs from (RHEL ships them as rpm packages, not in _bundled).
RUN python3 -m pip uninstall -y -q micropipenv pip \
    && rpm -e --nodeps python3.11-pip python3.11-pip-wheel python3-pip-wheel \
    && rm -f /opt/app-root/bin/pip* /opt/app-root/bin/micropipenv \
    && mkdir -p /opt/runner \
    && python3 /tmp/list-libraries.py > /opt/runner/libraries.txt \
    && rm /tmp/list-libraries.py
USER 1001
ENTRYPOINT []
CMD ["run-analysis"]
