# Base: Open Data Hub 2025a data-science runtime (Python 3.11), tag
# odh-pipeline-runtime-datascience-cpu-py311-ubi9:2025a-v1.35. PLACEHOLDER: the SPE
# runs Open Data Hub 2025b with Python 3.12 (s2i-generic-data-science-notebook:2025.2)
# plus ACRO 1.1.0 with SPMS's thresholds. Move to the 2025b py312 runtime image and
# add ACRO before PoC task 2.4 runs researcher code here; see runner/README.md,
# "Base image".
FROM --platform=linux/amd64 quay.io/opendatahub/odh-pipeline-runtime-datascience-cpu-py311-ubi9@sha256:62ceae2f54e06b7231027d1fed7392cfb7cbf5952929fc85b4342e3cb75fab75 AS base

FROM base AS test
RUN pip install pytest==8.3.3
COPY src/run_analysis /opt/runner/src/run_analysis
COPY tests /opt/runner/tests
ENV PYTHONPATH=/opt/runner/src
WORKDIR /opt/runner
ENTRYPOINT []
CMD ["python3", "-m", "pytest", "-q", "-p", "no:cacheprovider", "tests"]

FROM base AS runner
LABEL org.opencontainers.image.title="python-runner" \
      org.opencontainers.image.base.name="quay.io/opendatahub/odh-pipeline-runtime-datascience-cpu-py311-ubi9:2025a-v1.35" \
      org.opencontainers.image.base.digest="sha256:62ceae2f54e06b7231027d1fed7392cfb7cbf5952929fc85b4342e3cb75fab75"
USER 0
COPY src/run_analysis /tmp/run_analysis
# Only run-analysis: check-analysis, convert-notebook and the templates (in the package) are for the researcher's SPE.
COPY --chmod=0755 bin/run-analysis /opt/app-root/bin/run-analysis
COPY list-libraries.py /tmp/list-libraries.py
RUN cp -r /tmp/run_analysis "$(python3 -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')/run_analysis" \
    && rm -rf /tmp/run_analysis
# Researcher code must not install packages. Remove every installer: the venv's
# pip, uv, micropipenv and virtualenv (which carries its own pip wheel), the
# system Python 3.11's pip, and the pip and setuptools wheels that ensurepip
# installs from (RHEL ships them as rpm packages, not in ensurepip/_bundled).
RUN python3 -m pip uninstall -y -q uv micropipenv virtualenv pip \
    && rpm -e --nodeps python3.11-pip python3.11-pip-wheel python3-pip-wheel \
         python3.11-setuptools-wheel python3-setuptools-wheel \
    && rm -f /opt/app-root/bin/pip* /opt/app-root/bin/uv /opt/app-root/bin/uvx \
         /opt/app-root/bin/micropipenv /opt/app-root/bin/virtualenv \
    && mkdir -p /opt/runner \
    && python3 /tmp/list-libraries.py > /opt/runner/libraries.txt \
    && rm /tmp/list-libraries.py
# OpenShift runs the pod as a random user ID in group 0, so /outputs is
# writable by group 0, not only by user 1001.
RUN mkdir -p /outputs && chown 1001:0 /outputs && chmod 0775 /outputs
# The root file system is read-only in the pod, so libraries that need a scratch or cache directory (matplotlib,
# tempfile, joblib) use /tmp, which the pod mounts as a writable emptyDir. It is never part of the output.
ENV TMPDIR=/tmp MPLCONFIGDIR=/tmp/matplotlib
USER 1001
ENTRYPOINT []
CMD ["run-analysis"]
