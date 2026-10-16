# ==========================================
# Étape 1 : Compilation du projet Angular
# ==========================================
FROM node:22-alpine AS build
WORKDIR /app

# Copie des sources et manifestes
COPY . .

# Installation des dépendances si non présentes dans le contexte (ex: environnement CI vierge)
RUN if [ ! -d "node_modules" ]; then npm ci --omit=dev; fi

# Compilation de production Angular
RUN npm run build -- --configuration production

# ==========================================
# Étape 2 : Image d'exécution Nginx allégée
# ==========================================
FROM nginx:1.27-alpine AS runtime

# Copie des fichiers statiques compilés depuis l'étape de build
COPY --from=build /app/dist/janus.web/browser /usr/share/nginx/html

# Configuration Nginx (SPA routing + proxying API Janus)
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80

HEALTHCHECK --interval=15s --timeout=3s --retries=3 \
  CMD wget --quiet --tries=1 --spider http://localhost/ || exit 1

CMD ["nginx", "-g", "daemon off;"]
