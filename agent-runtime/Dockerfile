FROM docker:27-cli

RUN apk add --no-cache bash ca-certificates curl git jq openssh-client

COPY entrypoint.sh /usr/local/bin/construct-entrypoint
RUN chmod +x /usr/local/bin/construct-entrypoint

ENTRYPOINT ["/usr/local/bin/construct-entrypoint"]
