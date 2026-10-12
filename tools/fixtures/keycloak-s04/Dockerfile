ARG KEYCLOAK_BASE=quay.io/keycloak/keycloak:26.7.1
FROM ${KEYCLOAK_BASE}
ARG KEYCLOAK_BASE
LABEL loganalyzer.fixture="keycloak-s04" loganalyzer.fixture.base="${KEYCLOAK_BASE}"
ENV KC_DB=postgres KC_HEALTH_ENABLED=true
RUN /opt/keycloak/bin/kc.sh build
