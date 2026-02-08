FROM node:22-slim AS build
RUN corepack enable && corepack prepare pnpm@8.7.5 --activate
WORKDIR /app
COPY package.json pnpm-lock.yaml .npmrc ./
RUN pnpm install --frozen-lockfile
COPY . .
RUN pnpm build

FROM node:22-slim
WORKDIR /app
COPY --from=build /app/build ./build
COPY --from=build /app/package.json .
COPY --from=build /app/node_modules ./node_modules
EXPOSE 3000
ENV PORT=3000
CMD ["node", "build"]
