FROM eclipse-temurin:21-jre-alpine@sha256:974b08960c5d96694c780e65b2d5705268ab1e1ca1a0dd0caf4ba6c3fe34d699

ARG ACTIVEMQ_VERSION=6.3.2
ARG ACTIVEMQ_SHA512=30df66a98916fd448db6f29d591b24dd75584f58bafbe059377df923a65190a9dabfa036503bd35bc1469546360704e6942c5f922322e2b89e261bd45cad128a
RUN apk add --no-cache curl tar && \
    curl -fsSLO https://dlcdn.apache.org/activemq/${ACTIVEMQ_VERSION}/apache-activemq-${ACTIVEMQ_VERSION}-bin.tar.gz && \
    echo "${ACTIVEMQ_SHA512}  apache-activemq-${ACTIVEMQ_VERSION}-bin.tar.gz" | sha512sum -c - && \
    tar -xzf apache-activemq-${ACTIVEMQ_VERSION}-bin.tar.gz -C /opt && \
    ln -s /opt/apache-activemq-${ACTIVEMQ_VERSION} /opt/activemq && \
    rm apache-activemq-${ACTIVEMQ_VERSION}-bin.tar.gz

COPY activemq/activemq.xml /opt/activemq/conf/activemq.xml
EXPOSE 1883 8161
CMD ["/opt/activemq/bin/activemq", "console"]
