# ==========================================
# Étape 1 : Compilation du projet Angular
# ==========================================
FROM node:22-alpine AS build
WORKDIR /app

# Copie des sources et manifestes
COPY . .

# Installation des dépendances si non présentes dans le contexte (ex: environnement CI vierge)
RUN if [ ! -d "node_modules" ]; then npm ci --ignore-scripts; fi

# Compilation de production Angular
RUN npm run build -- --configuration production

# ==========================================
# Étape 2 : Image d'exécution Nginx allégée et durcie (non-root)
# ==========================================
FROM nginx:1.27-alpine AS runtime

# Préparation des répertoires d'exécution et permissions pour l'utilisateur non privilégié nginx
RUN touch /run/nginx.pid && \
    chown -R nginx:nginx /run/nginx.pid /var/cache/nginx /var/log/nginx

# Copie des fichiers statiques compilés (détenus par root, lecture seule pour nginx)
COPY --from=build /app/dist/janus.web/browser /usr/share/nginx/html

# Configuration Nginx (détenue par root, lecture seule pour nginx)
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Bascule sur l'utilisateur non-root nginx (UID 101)
USER nginx

EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=3s --retries=3 \
  CMD wget --quiet --tries=1 --spider http://localhost:8080/ || exit 1

CMD ["nginx", "-g", "daemon off;"]
