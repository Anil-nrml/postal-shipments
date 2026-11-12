FROM node:22-alpine AS build

WORKDIR /workspace

COPY . .
RUN npm ci

ARG WORKSPACE=@postal/shell
ARG APP_DIR=apps/shell
ARG VITE_REMOTE_AUTH
ARG VITE_REMOTE_SHIPMENTS
ARG VITE_REMOTE_POST_OFFICES
ARG VITE_REMOTE_TRACKING

ENV VITE_REMOTE_AUTH=${VITE_REMOTE_AUTH} \
    VITE_REMOTE_SHIPMENTS=${VITE_REMOTE_SHIPMENTS} \
    VITE_REMOTE_POST_OFFICES=${VITE_REMOTE_POST_OFFICES} \
    VITE_REMOTE_TRACKING=${VITE_REMOTE_TRACKING}

RUN npm run build --workspace="${WORKSPACE}"

FROM nginx:1.27-alpine

COPY nginx.conf /etc/nginx/conf.d/default.conf
ARG APP_DIR=apps/shell
COPY --from=build /workspace/${APP_DIR}/dist/ /usr/share/nginx/html/

EXPOSE 80
